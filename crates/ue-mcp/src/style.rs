//! Read-only, bounded source snapshot for an already-resolved symbol, not an indexer.
use serde_json::json;
use std::{
    fs,
    io::Read,
    path::{Path, PathBuf},
};
use ue_core::{CoreError, ErrorCode, JobContext};
const MAX_STYLE_BYTES: u64 = 1024 * 1024;

fn check_path(file: &Path, roots: &[String]) -> ue_core::Result<()> {
    if !file.is_absolute() {
        return Err(CoreError::new(
            ErrorCode::OutsideRoots,
            "source is not absolute",
        ));
    }
    // Reject links/reparse points at every component, not just at the leaf.
    let mut partial = PathBuf::new();
    for part in file.components() {
        partial.push(part);
        if matches!(part, std::path::Component::Prefix(_)) {
            continue;
        }
        let metadata = fs::symlink_metadata(&partial)?;
        #[cfg(windows)]
        let redirected = {
            use std::os::windows::fs::MetadataExt;
            metadata.file_attributes() & 0x400 != 0
        };
        #[cfg(not(windows))]
        let redirected = false;
        if metadata.file_type().is_symlink() || redirected {
            return Err(CoreError::new(
                ErrorCode::ExcludedPath,
                "redirected source path rejected",
            ));
        }
    }
    let canonical = fs::canonicalize(file)?;
    let mut contained = false;
    for root in roots {
        if canonical.starts_with(fs::canonicalize(root)?) {
            contained = true;
            break;
        }
    }
    if !contained {
        return Err(CoreError::new(
            ErrorCode::OutsideRoots,
            "source is outside launch-configured roots",
        ));
    }
    Ok(())
}

/// Only called on core's serial blocking worker, with a core-resolved file.
pub(crate) fn run(
    context: JobContext,
    file: String,
    revision: u64,
    naming: bool,
    limit: usize,
) -> ue_core::Result<serde_json::Value> {
    context.cancellation.check()?;
    if context.revision != revision {
        return Err(CoreError::new(
            ErrorCode::StaleReference,
            "index revision changed before style check; search again",
        ));
    }
    let path = Path::new(&file);
    check_path(path, &context.roots)?;
    let handle = fs::File::open(path)?;
    let before = handle.metadata()?;
    if !before.is_file() || before.len() > MAX_STYLE_BYTES {
        return Err(CoreError::new(
            ErrorCode::InvalidInput,
            "style source must be a regular file at most 1 MiB",
        ));
    }
    let mut bytes = Vec::new();
    (&handle)
        .take(MAX_STYLE_BYTES + 1)
        .read_to_end(&mut bytes)?;
    if bytes.len() as u64 > MAX_STYLE_BYTES {
        return Err(CoreError::new(
            ErrorCode::ResultTooLarge,
            "style source exceeds 1 MiB",
        ));
    }
    check_path(path, &context.roots)?;
    let after = handle.metadata()?;
    if before.len() != after.len() || before.modified()? != after.modified()? {
        return Err(CoreError::new(
            ErrorCode::StaleReference,
            "source changed during style read; try again",
        ));
    }
    let source = String::from_utf8(bytes)
        .map_err(|_| CoreError::new(ErrorCode::InvalidInput, "style source is not UTF-8"))?;
    context.cancellation.check()?;
    let parsed = ue_core::parse::parse_source(&source);
    let config = ue_style::StyleConfig {
        struct_prefix: naming,
        enum_prefix: naming,
        bool_property_prefix: naming,
        ..Default::default()
    };
    let mut diagnostics = ue_style::check(&source, &parsed, &config);
    let truncated = diagnostics.len() > limit;
    diagnostics.truncate(limit);
    context.cancellation.check()?;
    // DTO keys are camelCase even though style's library API uses snake_case.
    let diagnostics: Vec<_> = diagnostics.into_iter().map(|d| json!({"code":d.code,"severity":d.severity,"message":d.message,"byteRange":{"start":d.byte_range.start,"end":d.byte_range.end}})).collect();
    Ok(
        json!({"apiVersion":1,"sessionId":context.session_id,"revision":revision,"file":file,"diagnostics":diagnostics,"truncated":truncated,"snapshot":"disk"}),
    )
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn reject_outside_roots() {
        let root = tempfile::tempdir().unwrap();
        let other = tempfile::tempdir().unwrap();
        let file = other.path().join("outside.h");
        fs::write(&file, "UCLASS() class UEscape {};").unwrap();
        assert!(check_path(&file, &[root.path().to_string_lossy().into()]).is_err());
    }
    #[cfg(unix)]
    #[test]
    fn reject_symlink_even_with_in_root_target() {
        let root = tempfile::tempdir().unwrap();
        let real = root.path().join("real.h");
        fs::write(&real, "").unwrap();
        let link = root.path().join("link.h");
        std::os::unix::fs::symlink(real, &link).unwrap();
        assert!(check_path(&link, &[root.path().to_string_lossy().into()]).is_err());
    }
}
