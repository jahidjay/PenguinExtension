//! Rows as the rest of the workspace sees them.

use std::path::Path;

use ue_parser::{SymbolMetadata, UnrealSymbol};

/// A header the indexer has seen, with the fingerprint used to decide whether
/// it needs re-parsing.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FileEntry {
    /// Normalized path — see [`path_key`].
    pub path: String,
    /// Modification time in whatever unit the caller chose; only compared for
    /// equality, never interpreted.
    pub mtime: i64,
    pub hash: String,
}

impl FileEntry {
    /// Builds an entry by hashing the file's contents.
    pub fn new(path: &Path, mtime: i64, contents: &str) -> Self {
        FileEntry {
            path: path_key(path),
            mtime,
            hash: content_hash(contents),
        }
    }
}

/// A symbol read back out of the database, paired with the file it came from.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StoredSymbol {
    /// Normalized path of the declaring header.
    pub file: String,
    pub symbol: UnrealSymbol,
}

/// Opt-in rich read result; old `StoredSymbol` literals stay source compatible.
/// Rows are never merged by name. Metadata is absent for symbol-only writes or
/// an outdated extraction generation, and has optional fields for unknowns.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct StoredSymbolWithMetadata {
    pub file: String,
    pub symbol: UnrealSymbol,
    pub metadata: Option<SymbolMetadata>,
}

/// FNV-1a over the file contents, rendered as 16 hex digits.
///
/// Not cryptographic — this only has to notice that a header changed, and
/// avoiding a hashing dependency keeps the build light.
pub fn content_hash(contents: &str) -> String {
    const OFFSET: u64 = 0xcbf2_9ce4_8422_2325;
    const PRIME: u64 = 0x0000_0100_0000_01b3;

    let mut hash = OFFSET;
    for byte in contents.as_bytes() {
        hash ^= u64::from(*byte);
        hash = hash.wrapping_mul(PRIME);
    }
    format!("{hash:016x}")
}

/// Canonical spelling of a path for the `files.path` unique index.
///
/// Separators are normalized so the same header indexed through a Windows path
/// and a URI-derived path collides as intended. Casing is left alone: clients
/// are consistent within a session, and lowercasing would corrupt paths on the
/// case-sensitive platforms this daemon also targets.
pub fn path_key(path: &Path) -> String {
    path.to_string_lossy().replace('\\', "/")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hash_is_stable_and_content_sensitive() {
        assert_eq!(content_hash("abc"), content_hash("abc"));
        assert_ne!(content_hash("abc"), content_hash("abd"));
        assert_eq!(content_hash("abc").len(), 16);
    }

    #[test]
    fn path_separators_are_normalized() {
        assert_eq!(
            path_key(Path::new(r"C:\Game\Source\MyActor.h")),
            "C:/Game/Source/MyActor.h"
        );
    }
}
