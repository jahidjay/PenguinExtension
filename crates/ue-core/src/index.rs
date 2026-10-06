//! Synchronous, blocking disk indexing. Async callers must use `spawn_blocking`.
//!
//! Headers are limited to 16 MiB and must be UTF-8 (NUL bytes are rejected).
//! A UTF-8 BOM is retained, so stored byte ranges still refer to the on-disk
//! source. UTF-16 and legacy encodings are errors, not lossy conversions.
//! Files are only replaced after a successful read; pruning is restricted to
//! completely scanned roots and confirmed missing, non-excluded headers.

use std::collections::BTreeSet;
use std::fs::{self, File, Metadata};
use std::io::{self, Read};
use std::path::{Component, Path, PathBuf};
use std::time::UNIX_EPOCH;

use ue_db::{Db, FileEntry};

use crate::parse::parse_source_with_metadata;

const MAX_HEADER_BYTES: u64 = 16 * 1024 * 1024;
const EXCLUDED_DIRS: &[&str] = &[
    ".git",
    ".vs",
    "target",
    "node_modules",
    "Binaries",
    "Intermediate",
    "Saved",
    "DerivedDataCache",
];

/// Counts files, not symbols. Ignored paths do not affect the counters.
#[derive(Debug, Default)]
pub struct IndexReport {
    pub indexed: usize,
    pub unchanged: usize,
    pub removed: usize,
    pub errors: Vec<String>,
}

impl IndexReport {
    fn error(&mut self, path: &Path, error: impl std::fmt::Display) {
        self.errors.push(format!("{}: {error}", path.display()));
    }

    fn finish(mut self) -> Self {
        self.errors.sort();
        self.errors.dedup();
        self
    }
}

/// Indexes eligible headers under the roots in deterministic path order.
///
/// Overlapping roots are scanned once. Missing, unreadable or incompletely
/// traversed roots never authorize pruning. Other, successfully scanned roots
/// may still be pruned. Symbol replacement is atomic through `Db::replace_file`.
/// Symlinks and Windows reparse points (including junctions) are not traversed,
/// even when supplied explicitly as a root or as an ancestor of one.
pub fn index_workspace(db: &Db, roots: &[PathBuf]) -> IndexReport {
    index_workspace_cancellable(db, roots, &std::sync::atomic::AtomicBool::new(false))
}

/// Cooperative cancellation at directory/file boundaries. A cancelled scan
/// never starts pruning; any successful per-file transactions remain valid.
pub fn index_workspace_cancellable(
    db: &Db,
    roots: &[PathBuf],
    cancelled: &std::sync::atomic::AtomicBool,
) -> IndexReport {
    let stop = || cancelled.load(std::sync::atomic::Ordering::Acquire);
    let mut report = IndexReport::default();
    let mut resolved = BTreeSet::new();
    for root in roots {
        if stop() {
            return report.finish();
        }
        match resolve_path(root) {
            Ok(Some(path)) => match fs::symlink_metadata(&path) {
                Ok(meta) if meta.is_dir() && !is_link(&meta) => {
                    resolved.insert(path);
                }
                Ok(_) => report.error(root, "workspace root is not a directory"),
                Err(error) => report.error(root, error),
            },
            Ok(None) => {}
            Err(error) => report.error(root, error),
        }
    }

    // A parent sorts before its descendants; component-aware checks avoid
    // treating siblings such as Source and SourceExtra as overlapping roots.
    let mut unique: Vec<PathBuf> = Vec::new();
    for root in resolved {
        if !unique.iter().any(|parent| root.starts_with(parent)) {
            unique.push(root);
        }
    }
    let mut seen = BTreeSet::new();
    let mut complete = Vec::new();
    for root in unique {
        if stop() {
            return report.finish();
        }
        if scan_root(db, &root, &mut seen, &mut report, &stop) {
            complete.push(root);
        }
    }
    if !stop() && !complete.is_empty() {
        prune(db, &complete, &seen, &mut report, &stop);
    }
    report.finish()
}

/// Indexes one header, or removes its row if it is confirmed absent.
///
/// Non-headers, generated headers and excluded/link paths are ignored. An I/O,
/// encoding, size or database error leaves the previous symbols untouched.
/// Existing paths are canonicalized; a deleted path uses its closest existing
/// ancestor to retain the same database spelling (including Windows prefixes).
pub fn index_file(db: &Db, path: &Path) -> IndexReport {
    let mut report = IndexReport::default();
    if !is_header(path) {
        return report;
    }
    match resolve_path(path) {
        Ok(Some(path)) => match fs::symlink_metadata(&path) {
            Ok(meta) if is_link(&meta) => {}
            Ok(_) => index_existing(db, &path, &mut report),
            Err(error) if error.kind() == io::ErrorKind::NotFound => {
                remove(db, &path, &mut report);
            }
            Err(error) => report.error(&path, error),
        },
        Ok(None) => {}
        Err(error) => report.error(path, error),
    }
    report.finish()
}

pub(crate) fn is_header(path: &Path) -> bool {
    let Some(name) = path.file_name().and_then(|name| name.to_str()) else {
        return false;
    };
    let name = name.to_ascii_lowercase();
    (name.ends_with(".h") || name.ends_with(".hpp")) && !name.ends_with(".generated.h")
}

fn excluded_dir(path: &Path) -> bool {
    path.file_name()
        .and_then(|name| name.to_str())
        .is_some_and(|name| {
            EXCLUDED_DIRS
                .iter()
                .any(|excluded| name.eq_ignore_ascii_case(excluded))
        })
}

fn is_link(meta: &Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        // is_symlink alone misses junctions and other redirecting reparse tags.
        const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x400;
        if meta.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
            return true;
        }
    }
    meta.file_type().is_symlink()
}

/// Checks ancestors before canonicalizing, which would otherwise hide links.
/// None means intentionally excluded, not missing. Do not use lossy path keys:
/// two distinct non-Unicode paths could otherwise overwrite the same DB row.
pub(crate) fn resolve_path(path: &Path) -> io::Result<Option<PathBuf>> {
    // Windows absolute() folds parent components, potentially hiding a missing
    // directory or reparse point in `alias/../file`. Inspect the original walk
    // first, while still accepting legitimate existing-directory aliases.
    if path.components().any(|part| part == Component::ParentDir) {
        let original = if path.is_absolute() {
            path.to_owned()
        } else {
            std::env::current_dir()?.join(path)
        };
        let mut walked = PathBuf::new();
        for component in original.components() {
            if component == Component::ParentDir {
                let meta = fs::symlink_metadata(&walked)?;
                if !meta.is_dir() || is_link(&meta) {
                    return Err(io::Error::other(
                        "parent traversal requires a real existing directory",
                    ));
                }
            }
            walked.push(component.as_os_str());
            if matches!(component, Component::Prefix(_)) {
                continue;
            }
            if excluded_dir(&walked) {
                return Ok(None);
            }
            match fs::symlink_metadata(&walked) {
                Ok(meta) if is_link(&meta) => return Ok(None),
                Ok(_) => {}
                Err(e) if e.kind() == io::ErrorKind::NotFound => {}
                Err(e) => return Err(e),
            }
        }
    }
    let absolute = std::path::absolute(path)?;
    if absolute.to_str().is_none() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "path is not Unicode",
        ));
    }
    let ancestors: Vec<_> = absolute.ancestors().collect();
    let mut existing = None;
    for ancestor in ancestors.into_iter().rev() {
        if excluded_dir(ancestor) {
            return Ok(None);
        }
        match fs::symlink_metadata(ancestor) {
            Ok(meta) => {
                if is_link(&meta) {
                    return Ok(None);
                }
                existing = Some(ancestor);
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => break,
            Err(error) => return Err(error),
        }
    }
    let Some(existing) = existing else {
        return Err(io::Error::new(
            io::ErrorKind::NotFound,
            "no accessible path ancestor",
        ));
    };
    let suffix = absolute.strip_prefix(existing).map_err(io::Error::other)?;
    // A missing directory followed by a parent component cannot be resolved
    // safely: lexical normalization could delete an unrelated row.
    if suffix.components().any(|part| part == Component::ParentDir) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "unresolved parent directory",
        ));
    }
    if suffix
        .components()
        .any(|part| excluded_dir(Path::new(part.as_os_str())))
    {
        return Ok(None);
    }
    let mut canonical = fs::canonicalize(existing)?;
    if !suffix.as_os_str().is_empty() {
        canonical.push(suffix);
    }
    Ok(Some(canonical))
}

fn scan_root(
    db: &Db,
    root: &Path,
    seen: &mut BTreeSet<PathBuf>,
    report: &mut IndexReport,
    stop: &impl Fn() -> bool,
) -> bool {
    let before = report.errors.len();
    let mut pending = vec![root.to_path_buf()];
    let mut directories = BTreeSet::new();
    while let Some(path) = pending.pop() {
        if stop() {
            return false;
        }
        let meta = match fs::symlink_metadata(&path) {
            Ok(meta) => meta,
            Err(error) => {
                report.error(&path, error);
                continue;
            }
        };
        if is_link(&meta) {
            continue;
        }
        if meta.is_dir() {
            if excluded_dir(&path) {
                continue;
            }
            let canonical = match resolve_path(&path) {
                Ok(Some(canonical)) if canonical == path => canonical,
                Ok(_) => {
                    report.error(&path, "directory changed during traversal");
                    continue;
                }
                Err(error) => {
                    report.error(&path, error);
                    continue;
                }
            };
            if !directories.insert(canonical) {
                report.error(&path, "directory visited twice; traversal incomplete");
                continue;
            }
            let entries = match fs::read_dir(&path) {
                Ok(entries) => entries,
                Err(error) => {
                    report.error(&path, error);
                    continue;
                }
            };
            let mut children = Vec::new();
            for entry in entries {
                if stop() {
                    return false;
                }
                match entry {
                    Ok(entry) => children.push(entry.path()),
                    Err(error) => report.error(&path, error),
                }
            }
            children.sort();
            pending.extend(children.into_iter().rev());
        } else if is_header(&path) && seen.insert(path.clone()) {
            index_existing(db, &path, report);
        }
    }
    report.errors.len() == before
}

fn index_existing(db: &Db, path: &Path, report: &mut IndexReport) {
    let result = (|| -> Result<bool, String> {
        let (source, mtime) = read_header(path).map_err(|error| error.to_string())?;
        let entry = FileEntry::new(path, mtime, &source);
        if !db
            .needs_metadata_reindex(path, entry.mtime, &entry.hash)
            .map_err(|e| e.to_string())?
        {
            return Ok(false);
        }
        let parsed = parse_source_with_metadata(&source);
        db.replace_file_with_metadata(&entry, &parsed)
            .map_err(|e| e.to_string())?;
        Ok(true)
    })();
    match result {
        Ok(true) => report.indexed += 1,
        Ok(false) => report.unchanged += 1,
        Err(error) => report.error(path, error),
    }
}

fn read_header(path: &Path) -> io::Result<(String, i64)> {
    // Recheck ancestors: a subtree may have been replaced since enumeration.
    if resolve_path(path)?.as_deref() != Some(path) {
        return Err(io::Error::other(
            "path changed or became excluded during indexing",
        ));
    }
    let meta = fs::symlink_metadata(path)?;
    if !meta.is_file() || is_link(&meta) {
        return Err(io::Error::other("not a regular header file"));
    }
    if meta.len() > MAX_HEADER_BYTES {
        return Err(io::Error::other("header exceeds the 16 MiB limit"));
    }
    let modified = meta.modified()?;
    let file = File::open(path)?;
    let mut bytes = Vec::new();
    // Cap the read as well as checking metadata: the file can grow while open.
    (&file).take(MAX_HEADER_BYTES + 1).read_to_end(&mut bytes)?;
    if bytes.len() as u64 > MAX_HEADER_BYTES {
        return Err(io::Error::other("header exceeds the 16 MiB limit"));
    }
    let after = fs::symlink_metadata(path)?;
    let opened = file.metadata()?;
    if resolve_path(path)?.as_deref() != Some(path)
        || is_link(&after)
        || !after.is_file()
        || after.len() != meta.len()
        || opened.len() != meta.len()
        || bytes.len() as u64 != meta.len()
        || after.modified()? != modified
        || opened.modified()? != modified
    {
        return Err(io::Error::other(
            "header changed while being read; retry indexing",
        ));
    }
    let source = String::from_utf8(bytes).map_err(|error| {
        io::Error::new(
            io::ErrorKind::InvalidData,
            format!("not valid UTF-8: {error}"),
        )
    })?;
    if source.contains(char::from(0)) {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "NUL bytes are not supported in headers",
        ));
    }
    // Signed nanoseconds allow pre-epoch mtimes; a content hash is still checked
    // on every pass, including on filesystems with coarse timestamp resolution.
    let nanos = match modified.duration_since(UNIX_EPOCH) {
        Ok(duration) => duration.as_nanos() as i128,
        Err(error) => -(error.duration().as_nanos() as i128),
    };
    let mtime = i64::try_from(nanos).map_err(|_| io::Error::other("mtime is out of range"))?;
    Ok((source, mtime))
}

fn remove(db: &Db, path: &Path, report: &mut IndexReport) {
    match db.remove_file(path) {
        Ok(true) => report.removed += 1,
        Ok(false) => {}
        Err(error) => report.error(path, error),
    }
}

fn prune(
    db: &Db,
    roots: &[PathBuf],
    seen: &BTreeSet<PathBuf>,
    report: &mut IndexReport,
    stop: &impl Fn() -> bool,
) {
    // A vanished or redirected root is not evidence that all its files were
    // deleted. Validate it again before using its completed traversal.
    let mut available = Vec::new();
    for root in roots {
        match resolve_path(root).and_then(|path| match path {
            Some(path) if path == *root => fs::symlink_metadata(path),
            _ => Err(io::Error::other("workspace root changed during indexing")),
        }) {
            Ok(meta) if meta.is_dir() && !is_link(&meta) => available.push(root),
            Ok(_) => report.error(root, "workspace root is no longer a directory"),
            Err(error) => report.error(root, error),
        }
    }
    if available.is_empty() {
        return;
    }
    let files = match db.files() {
        Ok(files) => files,
        Err(error) => {
            report.errors.push(format!("list indexed files: {error}"));
            return;
        }
    };
    for stored in files {
        if stop() {
            return;
        }
        // path_key uses slashes even for verbatim Windows prefixes. Restore
        // native separators so Path recognizes those prefixes correctly.
        #[cfg(windows)]
        let stored = stored.replace('/', std::path::MAIN_SEPARATOR_STR);
        let path = Path::new(&stored);
        if !is_header(path)
            || seen.contains(path)
            || path.components().any(|part| part == Component::ParentDir)
            || !available.iter().any(|root| path.starts_with(root))
        {
            continue;
        }
        match resolve_path(path) {
            Ok(Some(resolved)) if available.iter().any(|root| resolved.starts_with(root)) => {
                match fs::symlink_metadata(resolved) {
                    Err(error) if error.kind() == io::ErrorKind::NotFound => {
                        remove(db, path, report)
                    }
                    Err(error) => report.error(path, error),
                    Ok(_) => {} // Present but skipped is never equivalent to deleted.
                }
            }
            Ok(_) => {}
            Err(error) => report.error(path, error),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::parse::parse_source;
    use std::time::Duration;
    use tempfile::TempDir;

    const HEADER: &str = "UCLASS()\nclass AOldActor : public AActor\n{\n    GENERATED_BODY()\n    UPROPERTY(EditAnywhere)\n    int32 Health;\n};\n";

    fn fixture() -> (TempDir, Db, PathBuf) {
        let dir = tempfile::tempdir().unwrap();
        let root = dir.path().join("Game");
        fs::create_dir(&root).unwrap();
        let root = fs::canonicalize(root).unwrap();
        let db = Db::open(dir.path().join("index.db")).unwrap();
        (dir, db, root)
    }

    fn write_header(root: &Path, name: &str) -> PathBuf {
        let path = root.join(name);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(&path, HEADER).unwrap();
        path
    }

    fn assert_counts(report: &IndexReport, indexed: usize, unchanged: usize, removed: usize) {
        assert!(report.errors.is_empty(), "{:?}", report.errors);
        assert_eq!(
            (report.indexed, report.unchanged, report.removed),
            (indexed, unchanged, removed)
        );
    }

    #[test]
    fn fresh_workspace_and_unchanged_rescan() {
        let (_dir, db, root) = fixture();
        let header = write_header(&root, "Source/Actor.h");
        let empty = root.join("Empty.hpp");
        fs::write(&empty, "// no reflected declarations\n").unwrap();
        assert_counts(&index_workspace(&db, std::slice::from_ref(&root)), 2, 0, 0);
        assert_eq!(db.in_file(&header).unwrap().len(), 2);
        assert_eq!(db.files().unwrap().len(), 2);
        assert_counts(&index_workspace(&db, &[root]), 0, 2, 0);
        assert_counts(&index_file(&db, &header), 0, 1, 0);
    }

    #[test]
    fn changed_content_with_identical_mtime_replaces_symbols() {
        let (_dir, db, root) = fixture();
        let path = write_header(&root, "Actor.h");
        assert_counts(&index_file(&db, &path), 1, 0, 0);
        let modified = fs::metadata(&path).unwrap().modified().unwrap();
        let updated = HEADER.replace("AOldActor", "ANewActor");
        assert_eq!(updated.len(), HEADER.len());
        fs::write(&path, updated).unwrap();
        File::options()
            .write(true)
            .open(&path)
            .unwrap()
            .set_modified(modified)
            .unwrap();
        assert_eq!(fs::metadata(&path).unwrap().modified().unwrap(), modified);
        assert_counts(&index_file(&db, &path), 1, 0, 0);
        assert!(db.find("AOldActor").unwrap().is_empty());
        assert_eq!(db.find("ANewActor").unwrap().len(), 1);
        assert_eq!(db.find("Health").unwrap().len(), 1);
        assert_counts(&index_file(&db, &path), 0, 1, 0);
    }

    #[test]
    fn mtime_is_part_of_the_fingerprint() {
        let (_dir, db, root) = fixture();
        let path = write_header(&root, "Actor.h");
        assert_counts(&index_file(&db, &path), 1, 0, 0);
        let modified = fs::metadata(&path).unwrap().modified().unwrap();
        File::options()
            .write(true)
            .open(&path)
            .unwrap()
            .set_modified(modified + Duration::from_secs(2))
            .unwrap();
        assert_counts(&index_file(&db, &path), 1, 0, 0);
        assert_counts(&index_file(&db, &path), 0, 1, 0);
    }

    #[test]
    fn deleted_file_and_deleted_parent_remove_only_once() {
        let (_dir, db, root) = fixture();
        let path = write_header(&root, "Source/Actor.h");
        assert_counts(&index_file(&db, &path), 1, 0, 0);
        fs::remove_dir_all(root.join("Source")).unwrap();
        assert_counts(&index_file(&db, &path), 0, 0, 1);
        assert_counts(&index_file(&db, &path), 0, 0, 0);
        assert!(db.files().unwrap().is_empty());
        assert!(db.find("AOldActor").unwrap().is_empty());
    }

    #[test]
    fn pruning_is_scoped_to_successfully_scanned_roots() {
        let (_dir, db, root) = fixture();
        let inside = write_header(&root, "Source/Nested/Actor.h");
        let outside = write_header(&root, "SourceExtra/Actor.h");
        assert_counts(&index_workspace(&db, std::slice::from_ref(&root)), 2, 0, 0);
        fs::remove_dir_all(root.join("Source/Nested")).unwrap();
        fs::remove_file(&outside).unwrap();
        assert_counts(&index_workspace(&db, &[root.join("Source")]), 0, 0, 1);
        assert!(db.in_file(&inside).unwrap().is_empty());
        assert_eq!(db.in_file(&outside).unwrap().len(), 2);
        assert_counts(&index_workspace(&db, &[]), 0, 0, 0);
        assert_eq!(db.files().unwrap().len(), 1);
        assert_counts(&index_workspace(&db, &[root]), 0, 0, 1);
    }

    #[test]
    fn parent_components_cannot_escape_pruning_scope() {
        let (_dir, db, root) = fixture();
        let outside = root.join("..").join("Outside.h");
        db.replace_file(
            &FileEntry::new(&outside, 0, HEADER),
            &parse_source(HEADER).symbols,
        )
        .unwrap();
        assert_counts(&index_workspace(&db, &[root]), 0, 0, 0);
        assert_eq!(db.in_file(&outside).unwrap().len(), 2);
    }

    #[test]
    fn absent_or_non_directory_root_preserves_cache() {
        let (_dir, db, root) = fixture();
        let path = write_header(&root, "Source/Actor.h");
        assert_counts(&index_workspace(&db, std::slice::from_ref(&root)), 1, 0, 0);
        fs::remove_dir_all(&root).unwrap();
        let report = index_workspace(&db, std::slice::from_ref(&root));
        assert_eq!(report.errors.len(), 1);
        assert_eq!(report.removed, 0);
        assert_eq!(db.in_file(&path).unwrap().len(), 2);
        fs::write(&root, "not a directory").unwrap();
        let report = index_workspace(&db, &[root]);
        assert_eq!(report.errors.len(), 1);
        assert_eq!(report.removed, 0);
        assert_eq!(db.in_file(&path).unwrap().len(), 2);
    }

    #[test]
    fn exclusions_apply_to_scans_explicit_files_and_roots() {
        let (_dir, db, root) = fixture();
        for name in ["Source/Actor.H", "Plugins/Test/Source/Plugin.HpP"] {
            write_header(&root, name);
        }
        for dir in EXCLUDED_DIRS {
            let path = write_header(&root, &format!("{dir}/Actor.h"));
            assert_counts(&index_file(&db, &path), 0, 0, 0);
            assert_counts(
                &index_workspace(&db, &[path.parent().unwrap().to_path_buf()]),
                0,
                0,
                0,
            );
        }
        for name in [
            "Source/Actor.GENERATED.H",
            "Actor.cpp",
            "Actor.txt",
            "Source/iNtErMeDiAtE/Actor.h",
        ] {
            let path = write_header(&root, name);
            assert_counts(&index_file(&db, &path), 0, 0, 0);
        }
        assert_counts(&index_workspace(&db, &[root]), 2, 0, 0);
        assert_eq!(db.files().unwrap().len(), 2);
    }

    #[test]
    fn excluded_cached_files_are_not_pruned() {
        let (_dir, db, root) = fixture();
        for name in ["Intermediate/Actor.h", "Actor.generated.h", "Actor.cpp"] {
            let path = root.join(name);
            db.replace_file(
                &FileEntry::new(&path, 0, HEADER),
                &parse_source(HEADER).symbols,
            )
            .unwrap();
        }
        assert_counts(&index_workspace(&db, &[root]), 0, 0, 0);
        assert_eq!(db.files().unwrap().len(), 3);
    }

    #[test]
    fn overlapping_roots_and_aliases_are_deduplicated() {
        let (_dir, db, root) = fixture();
        let child = root.join("Source");
        write_header(&root, "Source/Actor.h");
        write_header(&root, "Plugins/Plugin.hpp");
        let roots = [
            child.clone(),
            root.clone(),
            child.join(".."),
            root.join("."),
            child,
        ];
        assert_counts(&index_workspace(&db, &roots), 2, 0, 0);
        assert_counts(
            &index_workspace(&db, &roots.into_iter().rev().collect::<Vec<_>>()),
            0,
            2,
            0,
        );
        assert_eq!(db.files().unwrap().len(), 2);
    }

    #[test]
    fn paths_with_spaces_unicode_and_noncanonical_spelling() {
        let (_dir, db, root) = fixture();
        let path = write_header(&root, "Source/My Game/英雄 café.HPP");
        assert_counts(&index_file(&db, &path), 1, 0, 0);
        let alias = path
            .parent()
            .unwrap()
            .join(".")
            .join(path.file_name().unwrap());
        assert_counts(&index_file(&db, &alias), 0, 1, 0);
        assert_eq!(
            db.files().unwrap(),
            vec![ue_db::path_key(&fs::canonicalize(&path).unwrap())]
        );
        fs::remove_file(&path).unwrap();
        assert_counts(&index_file(&db, &alias), 0, 0, 1);
    }

    #[test]
    fn invalid_encoding_preserves_symbols_and_disables_root_pruning() {
        let (_dir, db, root) = fixture();
        let path = write_header(&root, "Actor.h");
        let deleted = write_header(&root, "Deleted.h");
        assert_counts(&index_workspace(&db, std::slice::from_ref(&root)), 2, 0, 0);
        fs::remove_file(&deleted).unwrap();
        for bytes in [
            vec![0xff, 0xfe, 0x41, 0],
            vec![0xc3, 0x28],
            vec![b'A', 0, b'B', 0],
        ] {
            fs::write(&path, bytes).unwrap();
            let report = index_workspace(&db, std::slice::from_ref(&root));
            assert_eq!(report.errors.len(), 1);
            assert_eq!((report.indexed, report.removed), (0, 0));
            assert_eq!(db.in_file(&path).unwrap().len(), 2);
            assert_eq!(db.in_file(&deleted).unwrap().len(), 2);
            let report = index_file(&db, &path);
            assert_eq!(report.errors.len(), 1);
            assert_eq!(report.removed, 0);
        }
        fs::write(&path, HEADER).unwrap();
        let report = index_workspace(&db, &[root]);
        assert!(report.errors.is_empty());
        assert_eq!(report.removed, 1);
    }

    #[test]
    fn utf8_bom_is_retained_for_hashing_and_byte_offsets() {
        let (_dir, db, root) = fixture();
        let path = root.join("Actor.h");
        let source = format!("{}{}", char::from_u32(0xfeff).unwrap(), HEADER);
        fs::write(&path, &source).unwrap();
        assert_counts(&index_file(&db, &path), 1, 0, 0);
        let symbols = db.in_file(&path).unwrap();
        assert_eq!(symbols.len(), 2);
        assert_eq!(symbols[0].symbol.byte_range.start, 3);
        assert_eq!(symbols[0].symbol.line, 1);
        let (read, mtime) = read_header(&path).unwrap();
        assert_eq!(read, source);
        assert!(!db
            .needs_reindex(&path, mtime, &ue_db::content_hash(&source))
            .unwrap());
        assert_counts(&index_file(&db, &path), 0, 1, 0);
    }

    #[test]
    fn malformed_cpp_uses_parser_recovery_and_empty_source_clears_symbols() {
        let (_dir, db, root) = fixture();
        let path = write_header(&root, "Actor.h");
        assert_counts(&index_file(&db, &path), 1, 0, 0);
        let malformed = "UCLASS(\nclass {\nUPROPERTY()\n";
        fs::write(&path, malformed).unwrap();
        assert_counts(&index_file(&db, &path), 1, 0, 0);
        let actual: Vec<_> = db
            .in_file(&path)
            .unwrap()
            .into_iter()
            .map(|s| s.symbol)
            .collect();
        assert_eq!(actual, parse_source(malformed).symbols);
        fs::write(&path, "").unwrap();
        assert_counts(&index_file(&db, &path), 1, 0, 0);
        assert!(db.in_file(&path).unwrap().is_empty());
        assert_eq!(db.files().unwrap().len(), 1);
    }

    #[test]
    fn huge_headers_are_rejected_without_losing_previous_symbols() {
        let (_dir, db, root) = fixture();
        let path = write_header(&root, "Actor.h");
        assert_counts(&index_file(&db, &path), 1, 0, 0);
        File::options()
            .write(true)
            .open(&path)
            .unwrap()
            .set_len(MAX_HEADER_BYTES + 1)
            .unwrap();
        let report = index_file(&db, &path);
        assert_eq!((report.indexed, report.removed), (0, 0));
        assert_eq!(report.errors.len(), 1);
        assert!(report.errors[0].contains("16 MiB"));
        assert_eq!(db.in_file(&path).unwrap().len(), 2);
    }

    #[test]
    fn non_regular_header_is_not_a_deletion() {
        let (_dir, db, root) = fixture();
        let path = write_header(&root, "Actor.h");
        assert_counts(&index_file(&db, &path), 1, 0, 0);
        fs::remove_file(&path).unwrap();
        fs::create_dir(&path).unwrap();
        let report = index_file(&db, &path);
        assert_eq!(report.errors.len(), 1);
        assert_eq!(report.removed, 0);
        assert_counts(&index_workspace(&db, &[root]), 0, 0, 0);
        assert_eq!(db.in_file(&path).unwrap().len(), 2);
    }

    #[test]
    fn failure_in_one_root_does_not_prevent_other_root_cleanup() {
        let (_dir, db, root) = fixture();
        let good = write_header(&root, "Good/Actor.h");
        let bad = write_header(&root, "Bad/Actor.h");
        assert_counts(&index_workspace(&db, std::slice::from_ref(&root)), 2, 0, 0);
        fs::remove_file(&good).unwrap();
        fs::remove_dir_all(root.join("Bad")).unwrap();
        let report = index_workspace(&db, &[root.join("Bad"), root.join("Good")]);
        assert_eq!(report.errors.len(), 1);
        assert_eq!(report.removed, 1);
        assert!(db.in_file(&good).unwrap().is_empty());
        assert_eq!(db.in_file(&bad).unwrap().len(), 2);
    }

    #[test]
    fn errors_are_deterministic_and_duplicate_roots_report_once() {
        let (_dir, db, root) = fixture();
        let a = root.join("A.h");
        let z = root.join("Z.h");
        fs::write(z, [0xff]).unwrap();
        fs::write(a, [0xff]).unwrap();
        let roots = [root.join("Missing"), root.clone(), root.join("Missing")];
        let first = index_workspace(&db, &roots);
        let second = index_workspace(&db, &roots.into_iter().rev().collect::<Vec<_>>());
        assert_eq!(first.errors.len(), 3);
        assert_eq!(first.errors, second.errors);
        assert!(first.errors.windows(2).all(|pair| pair[0] < pair[1]));
    }

    #[cfg(windows)]
    #[test]
    fn sharing_violation_preserves_cache_and_suppresses_pruning() {
        use std::os::windows::fs::OpenOptionsExt;
        let (_dir, db, root) = fixture();
        let path = write_header(&root, "Actor.h");
        let deleted = write_header(&root, "Deleted.h");
        assert_counts(&index_workspace(&db, std::slice::from_ref(&root)), 2, 0, 0);
        fs::remove_file(&deleted).unwrap();
        let lock = File::options()
            .read(true)
            .share_mode(0)
            .open(&path)
            .unwrap();
        let single = index_file(&db, &path);
        assert_eq!(single.errors.len(), 1);
        assert_eq!((single.indexed, single.removed), (0, 0));
        let workspace = index_workspace(&db, std::slice::from_ref(&root));
        assert!(!workspace.errors.is_empty());
        assert_eq!(workspace.removed, 0);
        assert_eq!(db.in_file(&path).unwrap().len(), 2);
        assert_eq!(db.in_file(&deleted).unwrap().len(), 2);
        drop(lock);
        assert_counts(&index_workspace(&db, &[root]), 0, 1, 1);
    }

    #[cfg(windows)]
    #[test]
    fn junctions_including_cycles_and_explicit_roots_are_not_followed() {
        use std::process::Command;
        let (_dir, db, root) = fixture();
        let inside = write_header(&root, "Source/Actor.h");
        let outside = root.parent().unwrap().join("External");
        write_header(&outside, "External.h");
        for (name, destination) in [("Cycle", &root), ("Redirect", &outside)] {
            let link = root.join(name);
            // Junctions require neither Developer Mode nor symlink privilege.
            // PowerShell consumes verbatim paths without cmd.exe quoting rules.
            let script = format!(
                "New-Item -ItemType Junction -Path '{}' -Target '{}' -ErrorAction Stop | Out-Null",
                link.display().to_string().replace('\'', "''"),
                destination.display().to_string().replace('\'', "''")
            );
            let output = Command::new("powershell.exe")
                .args(["-NoProfile", "-NonInteractive", "-Command", &script])
                .output()
                .unwrap();
            assert!(
                output.status.success(),
                "{}",
                String::from_utf8_lossy(&output.stderr)
            );
            assert!(is_link(&fs::symlink_metadata(&link).unwrap()));
            assert_counts(&index_workspace(&db, &[link]), 0, 0, 0);
        }
        assert_counts(&index_workspace(&db, std::slice::from_ref(&root)), 1, 0, 0);
        assert_counts(&index_file(&db, &root.join("Redirect/External.h")), 0, 0, 0);
        assert_counts(&index_workspace(&db, &[root.join("Cycle/Source")]), 0, 0, 0);
        assert_eq!(db.files().unwrap(), vec![ue_db::path_key(&inside)]);
        // A subtree that used to contain cached symbols may later become a
        // junction. Neither direct indexing nor cleanup should touch its rows.
        let skipped = root.join("Redirect/Missing.h");
        db.replace_file(
            &FileEntry::new(&skipped, 0, HEADER),
            &parse_source(HEADER).symbols,
        )
        .unwrap();
        assert_counts(&index_workspace(&db, std::slice::from_ref(&root)), 0, 1, 0);
        assert_counts(&index_file(&db, &skipped), 0, 0, 0);
        assert_eq!(db.in_file(&skipped).unwrap().len(), 2);
        fs::remove_dir(root.join("Cycle")).unwrap();
        fs::remove_dir(root.join("Redirect")).unwrap();
    }

    #[cfg(unix)]
    #[test]
    fn symlinks_and_cycles_are_not_followed_or_pruned() {
        use std::os::unix::fs::symlink;
        let (_dir, db, root) = fixture();
        let real = write_header(&root, "Source/Actor.h");
        let replaced = write_header(&root, "Replaced.h");
        assert_counts(&index_workspace(&db, std::slice::from_ref(&root)), 2, 0, 0);
        fs::remove_file(&replaced).unwrap();
        symlink(root.join("Missing.h"), &replaced).unwrap();
        symlink(&root, root.join("Cycle")).unwrap();
        symlink(&real, root.join("Alias.h")).unwrap();
        assert_counts(&index_workspace(&db, std::slice::from_ref(&root)), 0, 1, 0);
        assert_counts(&index_file(&db, &replaced), 0, 0, 0);
        assert_counts(&index_file(&db, &root.join("Alias.h")), 0, 0, 0);
        assert_counts(&index_workspace(&db, &[root.join("Cycle/Source")]), 0, 0, 0);
        assert_eq!(db.in_file(&replaced).unwrap().len(), 2);
    }

    #[cfg(unix)]
    #[test]
    fn unreadable_directory_disables_pruning_for_the_whole_root() {
        use std::os::unix::fs::PermissionsExt;
        let (_dir, db, root) = fixture();
        let hidden = write_header(&root, "Hidden/Actor.h");
        let deleted = write_header(&root, "Deleted.h");
        assert_counts(&index_workspace(&db, std::slice::from_ref(&root)), 2, 0, 0);
        fs::remove_file(&deleted).unwrap();
        let directory = root.join("Hidden");
        let original = fs::metadata(&directory).unwrap().permissions();
        fs::set_permissions(&directory, fs::Permissions::from_mode(0)).unwrap();
        let denied = fs::read_dir(&directory).is_err();
        let report = index_workspace(&db, &[root]);
        fs::set_permissions(&directory, original).unwrap();
        // Elevated test runners can bypass mode bits; only assert denial when
        // the OS actually enforced it. Other failure tests are unconditional.
        if denied {
            assert!(!report.errors.is_empty());
            assert_eq!(report.removed, 0);
            assert_eq!(db.in_file(&hidden).unwrap().len(), 2);
            assert_eq!(db.in_file(&deleted).unwrap().len(), 2);
        }
    }
}
