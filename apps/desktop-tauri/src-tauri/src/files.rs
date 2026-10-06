use std::{
    fs::{self, File, Metadata},
    io::{self, Read},
    path::{Component, Path, PathBuf},
};
const MAX_BYTES: u64 = 1024 * 1024;
fn redirected(meta: &Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if meta.file_attributes() & 0x400 != 0 {
            return true;
        }
    }
    meta.file_type().is_symlink()
}
pub fn native(value: &str) -> PathBuf {
    #[cfg(windows)]
    {
        PathBuf::from(value.replace('/', std::path::MAIN_SEPARATOR_STR))
    }
    #[cfg(not(windows))]
    {
        PathBuf::from(value)
    }
}
fn resolve(path: &Path) -> Result<PathBuf, String> {
    if !path.is_absolute() || path.components().any(|c| c == Component::ParentDir) {
        return Err("An absolute path without parent traversal is required".into());
    }
    for ancestor in path.ancestors() {
        let meta = fs::symlink_metadata(ancestor).map_err(|e| e.to_string())?;
        if redirected(&meta) {
            return Err("Symlinks and junctions are not readable".into());
        }
    }
    fs::canonicalize(path).map_err(|e| e.to_string())
}
// Detect pathname swaps, but do not treat these non-atomic observations as a
// filesystem sandbox. Hard links and concurrent in-place writes remain possible.
fn open_no_follow(path: &Path, directory: bool) -> io::Result<File> {
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT,
        };
        let flags = FILE_FLAG_OPEN_REPARSE_POINT
            | if directory {
                FILE_FLAG_BACKUP_SEMANTICS
            } else {
                0
            };
        fs::OpenOptions::new()
            .read(true)
            .custom_flags(flags)
            .open(path)
    }
    #[cfg(unix)]
    {
        use rustix::fs::{open, Mode, OFlags};
        // NONBLOCK avoids hanging if a regular file is swapped for a FIFO.
        let flags = OFlags::RDONLY
            | OFlags::NOFOLLOW
            | OFlags::NONBLOCK
            | OFlags::CLOEXEC
            | if directory {
                OFlags::DIRECTORY
            } else {
                OFlags::empty()
            };
        open(path, flags, Mode::empty())
            .map(File::from)
            .map_err(Into::into)
    }
    #[cfg(not(any(windows, unix)))]
    {
        let _ = (path, directory);
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Handle validation is unavailable",
        ))
    }
}

// Query the live handle rather than canonicalizing its pathname: an ancestor
// can otherwise be replaced and restored while the handle points elsewhere.
fn opened_path(file: &File) -> io::Result<PathBuf> {
    #[cfg(windows)]
    {
        use std::{
            ffi::OsString,
            os::windows::{ffi::OsStringExt, io::AsRawHandle},
        };
        use windows_sys::Win32::Storage::FileSystem::{
            GetFinalPathNameByHandleW, FILE_NAME_NORMALIZED,
        };
        let mut buffer = vec![0u16; 512];
        loop {
            // SAFETY: file owns a live handle, and buffer is writable for the
            // supplied number of UTF-16 code units. No pointer escapes.
            let len = unsafe {
                GetFinalPathNameByHandleW(
                    file.as_raw_handle(),
                    buffer.as_mut_ptr(),
                    buffer.len() as u32,
                    FILE_NAME_NORMALIZED,
                )
            } as usize;
            if len == 0 {
                return Err(io::Error::last_os_error());
            }
            if len < buffer.len() {
                return Ok(PathBuf::from(OsString::from_wide(&buffer[..len])));
            }
            if len > 32768 {
                return Err(io::Error::new(
                    io::ErrorKind::InvalidData,
                    "Opened path is too long",
                ));
            }
            buffer.resize(len + 1, 0);
        }
    }
    #[cfg(target_os = "linux")]
    {
        use std::os::fd::AsRawFd;
        fs::read_link(format!("/proc/self/fd/{}", file.as_raw_fd()))
    }
    #[cfg(target_os = "macos")]
    {
        use std::{ffi::OsString, os::unix::ffi::OsStringExt};
        rustix::fs::getpath(file)
            .map(|path| PathBuf::from(OsString::from_vec(path.into_bytes())))
            .map_err(Into::into)
    }
    #[cfg(not(any(windows, target_os = "linux", target_os = "macos")))]
    {
        let _ = file;
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "Opened paths are unavailable",
        ))
    }
}

fn checked_handle(file: File, path: &Path, directory: bool) -> Result<same_file::Handle, String> {
    let meta = file.metadata().map_err(|e| e.to_string())?;
    if redirected(&meta)
        || (if directory {
            !meta.is_dir()
        } else {
            !meta.is_file()
        })
        || opened_path(&file).map_err(|e| e.to_string())? != path
    {
        return Err("Opened file is redirected or no longer at the validated path".into());
    }
    same_file::Handle::from_file(file).map_err(|e| e.to_string())
}

fn validate_handle(handle: &same_file::Handle, path: &Path, directory: bool) -> Result<(), String> {
    if opened_path(handle.as_file()).map_err(|e| e.to_string())? != path || resolve(path)? != path {
        return Err("Header or workspace moved during reading; retry".into());
    }
    // Keep both handles alive while comparing volume/file ID (Windows) or
    // device/inode (Unix). Equal sizes and timestamps do not prove identity.
    // same-file uses legacy 64-bit Windows IDs, not ReFS 128-bit IDs; this is
    // defense in depth alongside the handle path, not universal identity proof.
    let current = checked_handle(
        open_no_follow(path, directory).map_err(|e| e.to_string())?,
        path,
        directory,
    )?;
    if handle != &current {
        return Err("Header or workspace was replaced during reading; retry".into());
    }
    Ok(())
}

#[derive(Clone, Copy)]
enum ReadStage {
    Resolved,
    Opened,
    Read,
}

pub fn read_header(file: &str, roots: &[String]) -> Result<(String, String), String> {
    read_header_with_hook(file, roots, |_| {})
}

// A per-call hook makes race regression tests deterministic without global state.
fn read_header_with_hook(
    file: &str,
    roots: &[String],
    hook: impl Fn(ReadStage),
) -> Result<(String, String), String> {
    if file.len() > 4096 || file.contains(char::from(0)) {
        return Err("Invalid header path".into());
    }
    let path = native(file);
    let name = path
        .file_name()
        .and_then(|s| s.to_str())
        .unwrap_or_default()
        .to_ascii_lowercase();
    if !(name.ends_with(".h") || name.ends_with(".hpp")) || name.ends_with(".generated.h") {
        return Err("Only non-generated .h/.hpp files are supported".into());
    }
    let canonical = resolve(&path)?;
    let roots: Result<Vec<_>, String> = roots
        .iter()
        .map(|root| {
            let root = resolve(&native(root))?;
            let handle = checked_handle(
                open_no_follow(&root, true).map_err(|e| e.to_string())?,
                &root,
                true,
            )?;
            Ok((root, handle))
        })
        .collect();
    let roots = roots?;
    let (root, root_handle) = roots
        .iter()
        .find(|(root, _)| canonical.starts_with(root))
        .ok_or("Header is outside the workspace")?;
    hook(ReadStage::Resolved);
    let opened = open_no_follow(&canonical, false).map_err(|e| e.to_string())?;
    hook(ReadStage::Opened);
    let handle = checked_handle(opened, &canonical, false)?;
    validate_handle(root_handle, root, true)?;
    validate_handle(&handle, &canonical, false)?;
    let opened = handle.as_file();
    let before = opened.metadata().map_err(|e| e.to_string())?;
    if before.len() > MAX_BYTES {
        return Err("Style checks require a regular header of at most 1 MiB".into());
    }
    let modified = before.modified().map_err(|e| e.to_string())?;
    let mut bytes = Vec::new();
    opened
        .take(MAX_BYTES + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    hook(ReadStage::Read);
    validate_handle(&handle, &canonical, false)?;
    validate_handle(root_handle, root, true)?;
    let after = opened.metadata().map_err(|e| e.to_string())?;
    if bytes.len() as u64 > MAX_BYTES
        || resolve(&path)? != canonical
        || after.len() != before.len()
        || bytes.len() as u64 != before.len()
        || after.modified().map_err(|e| e.to_string())? != modified
    {
        return Err("Header changed during reading; retry".into());
    }
    let source = String::from_utf8(bytes).map_err(|_| "Header must be UTF-8")?;
    if source.contains(char::from(0)) {
        return Err("NUL bytes are not supported".into());
    }
    Ok((canonical.to_string_lossy().into_owned(), source))
}
#[cfg(test)]
mod tests {
    use super::*;
    fn directory_link(target: &Path, link: &Path) {
        #[cfg(windows)]
        {
            // Junction creation does not require Windows symlink privileges.
            let output = std::process::Command::new("cmd.exe")
                .args(["/d", "/c", "mklink", "/J"])
                .arg(link)
                .arg(target)
                .output()
                .unwrap();
            assert!(
                output.status.success(),
                "{}",
                String::from_utf8_lossy(&output.stderr)
            );
        }
        #[cfg(unix)]
        std::os::unix::fs::symlink(target, link).unwrap();
    }

    fn remove_directory_link(link: &Path) {
        #[cfg(windows)]
        fs::remove_dir(link).unwrap();
        #[cfg(unix)]
        fs::remove_file(link).unwrap();
    }

    fn matching_file(path: &Path, contents: &str, original: &Path) {
        fs::write(path, contents).unwrap();
        let modified = fs::metadata(original).unwrap().modified().unwrap();
        File::options()
            .write(true)
            .open(path)
            .unwrap()
            .set_times(fs::FileTimes::new().set_modified(modified))
            .unwrap();
        assert_eq!(
            fs::metadata(path).unwrap().len(),
            fs::metadata(original).unwrap().len()
        );
        assert_eq!(fs::metadata(path).unwrap().modified().unwrap(), modified);
    }

    #[test]
    fn ancestor_redirect_restored_after_open_is_rejected_before_reading() {
        let d = tempfile::tempdir().unwrap();
        let root = d.path().join("Game");
        let source = root.join("Source");
        let parked = root.join("Parked");
        let outside = d.path().join("Outside");
        fs::create_dir_all(&source).unwrap();
        fs::create_dir(&outside).unwrap();
        let path = source.join("Actor.h");
        fs::write(&path, "public").unwrap();
        matching_file(&outside.join("Actor.h"), "secret", &path);
        let read = std::cell::Cell::new(false);
        let result = read_header_with_hook(
            path.to_str().unwrap(),
            &[root.to_str().unwrap().into()],
            |stage| match stage {
                ReadStage::Resolved => {
                    fs::rename(&source, &parked).unwrap();
                    directory_link(&outside, &source);
                }
                ReadStage::Opened => {
                    remove_directory_link(&source);
                    fs::rename(&parked, &source).unwrap();
                }
                ReadStage::Read => read.set(true),
            },
        );
        assert!(
            result.is_err(),
            "outside handle must not be accepted: {result:?}"
        );
        assert!(!read.get(), "outside data must not be read");
        assert_eq!(fs::read_to_string(&path).unwrap(), "public");
    }

    #[test]
    fn same_size_same_timestamp_replacement_after_read_is_rejected() {
        let d = tempfile::tempdir().unwrap();
        let path = d.path().join("Actor.h");
        let replacement = d.path().join("Replacement.h");
        fs::write(&path, "public").unwrap();
        matching_file(&replacement, "secret", &path);
        let result = read_header_with_hook(
            path.to_str().unwrap(),
            &[d.path().to_str().unwrap().into()],
            |stage| {
                if matches!(stage, ReadStage::Read) {
                    fs::rename(&path, d.path().join("Parked.h")).unwrap();
                    fs::rename(&replacement, &path).unwrap();
                }
            },
        );
        assert!(
            result.is_err(),
            "same metadata must not hide a file swap: {result:?}"
        );
    }

    #[test]
    fn replaced_root_after_resolution_is_rejected() {
        let d = tempfile::tempdir().unwrap();
        let root = d.path().join("Game");
        let replacement = d.path().join("Replacement");
        fs::create_dir(&root).unwrap();
        fs::create_dir(&replacement).unwrap();
        let path = root.join("Actor.h");
        fs::write(&path, "public").unwrap();
        matching_file(&replacement.join("Actor.h"), "secret", &path);
        let read = std::cell::Cell::new(false);
        let result = read_header_with_hook(
            path.to_str().unwrap(),
            &[root.to_str().unwrap().into()],
            |stage| {
                if matches!(stage, ReadStage::Resolved) {
                    fs::rename(&root, d.path().join("Parked")).unwrap();
                    fs::rename(&replacement, &root).unwrap();
                }
                if matches!(stage, ReadStage::Read) {
                    read.set(true);
                }
            },
        );
        assert!(result.is_err());
        assert!(!read.get());
    }

    #[test]
    fn regular_file_replaced_with_directory_before_open_is_rejected() {
        let d = tempfile::tempdir().unwrap();
        let path = d.path().join("Actor.h");
        fs::write(&path, "public").unwrap();
        let result = read_header_with_hook(
            path.to_str().unwrap(),
            &[d.path().to_str().unwrap().into()],
            |stage| {
                if matches!(stage, ReadStage::Resolved) {
                    fs::remove_file(&path).unwrap();
                    fs::create_dir(&path).unwrap();
                }
                assert!(!matches!(stage, ReadStage::Read));
            },
        );
        assert!(result.is_err());
    }

    #[test]
    fn directory_reparse_point_replacing_leaf_is_not_followed() {
        let d = tempfile::tempdir().unwrap();
        let root = d.path().join("Game");
        let outside = d.path().join("Outside");
        fs::create_dir(&root).unwrap();
        fs::create_dir(&outside).unwrap();
        let path = root.join("Actor.h");
        fs::write(&path, "public").unwrap();
        let result = read_header_with_hook(
            path.to_str().unwrap(),
            &[root.to_str().unwrap().into()],
            |stage| {
                if matches!(stage, ReadStage::Resolved) {
                    fs::remove_file(&path).unwrap();
                    directory_link(&outside, &path);
                }
                assert!(!matches!(stage, ReadStage::Read));
            },
        );
        remove_directory_link(&path);
        assert!(result.is_err());
    }

    #[cfg(unix)]
    #[test]
    fn file_symlink_replacing_leaf_is_not_followed() {
        let d = tempfile::tempdir().unwrap();
        let path = d.path().join("Actor.h");
        let outside = d.path().join("Outside.h");
        fs::write(&path, "public").unwrap();
        fs::write(&outside, "secret").unwrap();
        let result = read_header_with_hook(
            path.to_str().unwrap(),
            &[d.path().to_str().unwrap().into()],
            |stage| {
                if matches!(stage, ReadStage::Resolved) {
                    fs::remove_file(&path).unwrap();
                    std::os::unix::fs::symlink(&outside, &path).unwrap();
                }
                assert!(!matches!(stage, ReadStage::Read));
            },
        );
        assert!(result.is_err());
    }

    #[test]
    fn growth_during_read_is_rejected_and_exact_limit_is_allowed() {
        let d = tempfile::tempdir().unwrap();
        let path = d.path().join("Actor.hpp");
        let roots = [d.path().to_str().unwrap().into()];
        fs::write(&path, vec![b'a'; MAX_BYTES as usize]).unwrap();
        let (canonical, contents) = read_header(path.to_str().unwrap(), &roots).unwrap();
        assert_eq!(PathBuf::from(canonical), fs::canonicalize(&path).unwrap());
        assert_eq!(contents.len() as u64, MAX_BYTES);
        let result = read_header_with_hook(path.to_str().unwrap(), &roots, |stage| {
            if matches!(stage, ReadStage::Read) {
                File::options()
                    .write(true)
                    .open(&path)
                    .unwrap()
                    .set_len(MAX_BYTES + 1)
                    .unwrap();
            }
        });
        assert!(result.is_err());
    }

    #[test]
    fn existing_directory_link_and_file_root_are_rejected() {
        let d = tempfile::tempdir().unwrap();
        let root = d.path().join("Game");
        let link = d.path().join("Linked");
        fs::create_dir(&root).unwrap();
        let path = root.join("Actor.h");
        fs::write(&path, "public").unwrap();
        assert!(read_header(path.to_str().unwrap(), &[path.to_str().unwrap().into()]).is_err());
        directory_link(&root, &link);
        let result = read_header(
            link.join("Actor.h").to_str().unwrap(),
            &[d.path().to_str().unwrap().into()],
        );
        remove_directory_link(&link);
        assert!(result.is_err());
    }

    #[test]
    fn bounded_workspace_files_only() {
        let d = tempfile::tempdir().unwrap();
        let root = d.path().join("Game");
        fs::create_dir(&root).unwrap();
        let roots = vec![root.to_str().unwrap().into()];
        let path = root.join("Actor.h");
        fs::write(&path, "UCLASS() class AActor {};").unwrap();
        assert!(read_header(path.to_str().unwrap(), &roots).is_ok());
        let outside = d.path().join("Outside.h");
        fs::write(&outside, "secret").unwrap();
        assert!(read_header(outside.to_str().unwrap(), &roots).is_err());
        assert!(read_header(root.join("../Outside.h").to_str().unwrap(), &roots).is_err());
        assert!(read_header("Actor.h", &roots).is_err());
        fs::write(&path, vec![b'a'; MAX_BYTES as usize + 1]).unwrap();
        assert!(read_header(path.to_str().unwrap(), &roots).is_err());
    }
    #[test]
    fn invalid_encoding_and_extension_are_rejected() {
        let d = tempfile::tempdir().unwrap();
        let roots = vec![d.path().to_str().unwrap().into()];
        for (name, bytes) in [
            ("A.h", vec![0xff]),
            ("B.hpp", vec![0]),
            ("C.txt", vec![b'a']),
            ("D.generated.h", vec![b'a']),
        ] {
            let p = d.path().join(name);
            fs::write(&p, bytes).unwrap();
            assert!(read_header(p.to_str().unwrap(), &roots).is_err());
        }
    }
}
