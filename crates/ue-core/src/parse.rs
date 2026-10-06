//! Parser access from async request handlers.
//!
//! [`ue_parser::UnrealParser`] needs `&mut self` to parse and owns a
//! `tree_sitter::Parser`, which is `Send` but not `Sync`. Rather than put one
//! behind a mutex and serialize every request, each blocking worker thread
//! keeps its own parser and reuses it. Callers reach this from
//! [`tokio::task::spawn_blocking`], which is where parsing belongs anyway —
//! it is CPU-bound and would otherwise stall the async executor.

use std::cell::{OnceCell, RefCell};

use ue_parser::{ParsedFile, UnrealParser};

thread_local! {
    /// One parser per worker thread, built on first use.
    ///
    /// `None` means construction failed — the tree-sitter grammar could not be
    /// loaded — and is cached so a broken install costs one attempt per thread
    /// instead of one per keystroke.
    static PARSER: OnceCell<RefCell<Option<UnrealParser>>> = const { OnceCell::new() };
}

/// Parses `source` on the calling thread.
///
/// Returns an empty [`ParsedFile`] if the parser could not be created. A
/// document with no symbols degrades the server to "no completions here",
/// which is the right failure mode for an editor feature — far better than
/// refusing to serve the file at all.
pub fn parse_source(source: &str) -> ParsedFile {
    PARSER.with(|cell| {
        let parser = cell.get_or_init(|| RefCell::new(UnrealParser::new().ok()));
        let mut slot = parser.borrow_mut();
        match slot.as_mut() {
            Some(parser) => parser.parse(source),
            None => ParsedFile::default(),
        }
    })
}

/// Rich worker-local parse using the same parser instance and failure policy.
pub fn parse_source_with_metadata(source: &str) -> ue_parser::ParsedFileWithMetadata {
    PARSER.with(|cell| {
        let parser = cell.get_or_init(|| RefCell::new(UnrealParser::new().ok()));
        let mut slot = parser.borrow_mut();
        match slot.as_mut() {
            Some(parser) => parser.parse_with_metadata(source),
            None => ue_parser::ParsedFileWithMetadata::default(),
        }
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    const HEADER: &str = r#"
UCLASS(Blueprintable)
class AMyActor : public AActor
{
    GENERATED_BODY()

    UPROPERTY(EditAnywhere, Category = "Stats")
    float Health;
};
"#;

    #[test]
    fn parses_a_header_and_reuses_the_thread_local_parser() {
        let first = parse_source(HEADER);
        assert!(first.find("AMyActor").is_some(), "class not found");
        assert!(first.find("Health").is_some(), "property not found");

        // Second call on the same thread goes through the cached parser.
        let second = parse_source(HEADER);
        assert_eq!(first, second);
    }

    #[test]
    fn empty_source_yields_no_symbols() {
        assert!(parse_source("").symbols.is_empty());
    }
}
