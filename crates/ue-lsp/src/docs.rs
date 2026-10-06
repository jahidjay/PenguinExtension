//! The buffers the editor currently has open.
//!
//! Open documents are authoritative over the database: the user's unsaved
//! edits are what they expect completion and hover to reflect, and the indexed
//! copy on disk may be minutes old. Anything not open falls back to the store.

use std::collections::HashMap;
use std::sync::RwLock;

use tower_lsp::lsp_types::{Position, Url};
use ue_parser::ParsedFile;

use crate::parse::parse_source;
use crate::text::{self, LineIndex};

/// One open buffer, parsed and indexed for position lookups.
#[derive(Debug, Clone, Default)]
pub struct Document {
    pub text: String,
    pub parsed: ParsedFile,
    pub lines: LineIndex,
}

impl Document {
    /// Parses `text`. CPU-bound — call it from [`tokio::task::spawn_blocking`].
    pub fn parse(text: String) -> Self {
        let parsed = parse_source(&text);
        let lines = LineIndex::new(&text);
        Document {
            text,
            parsed,
            lines,
        }
    }

    /// Byte offset of an LSP position within this buffer.
    pub fn offset(&self, position: Position) -> Option<usize> {
        self.lines.offset(&self.text, position)
    }

    /// The identifier under (or immediately before) the caret.
    pub fn word_at(&self, position: Position) -> Option<&str> {
        let offset = self.offset(position)?;
        text::word_at(&self.text, offset).map(|(word, _, _)| word)
    }

    /// What the user has typed so far at the caret.
    pub fn prefix_at(&self, position: Position) -> &str {
        match self.offset(position) {
            Some(offset) => text::prefix_before(&self.text, offset),
            None => "",
        }
    }
}

/// Thread-safe map of open buffers.
///
/// Guarded by a plain [`std::sync::RwLock`] rather than the async one: no code
/// path awaits while holding it, and keeping it synchronous makes it impossible
/// to introduce a lock held across a suspension point later.
#[derive(Debug, Default)]
pub struct Documents {
    inner: RwLock<HashMap<Url, Document>>,
}

impl Documents {
    pub fn new() -> Self {
        Documents::default()
    }

    /// Inserts or replaces a buffer.
    pub fn insert(&self, uri: Url, document: Document) {
        self.write().insert(uri, document);
    }

    /// Drops a buffer, as on `textDocument/didClose`.
    pub fn remove(&self, uri: &Url) -> Option<Document> {
        self.write().remove(uri)
    }

    /// Runs `f` against a buffer without cloning it.
    pub fn with<R>(&self, uri: &Url, f: impl FnOnce(&Document) -> R) -> Option<R> {
        self.read().get(uri).map(f)
    }

    /// A copy of a buffer's text, for handing to a blocking parse.
    pub fn text_of(&self, uri: &Url) -> Option<String> {
        self.with(uri, |doc| doc.text.clone())
    }

    pub fn is_open(&self, uri: &Url) -> bool {
        self.read().contains_key(uri)
    }

    pub fn len(&self) -> usize {
        self.read().len()
    }

    pub fn is_empty(&self) -> bool {
        self.len() == 0
    }

    // A panicking request handler should not take the document store down with
    // it; the data behind the lock is still perfectly usable.
    fn read(&self) -> std::sync::RwLockReadGuard<'_, HashMap<Url, Document>> {
        self.inner.read().unwrap_or_else(|e| e.into_inner())
    }

    fn write(&self) -> std::sync::RwLockWriteGuard<'_, HashMap<Url, Document>> {
        self.inner.write().unwrap_or_else(|e| e.into_inner())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const HEADER: &str = "UCLASS()\nclass AMyActor : public AActor\n{\n};\n";

    fn uri() -> Url {
        Url::parse("file:///project/Source/MyActor.h").unwrap()
    }

    #[test]
    fn parsing_populates_symbols_and_line_index() {
        let doc = Document::parse(HEADER.to_string());
        assert!(doc.parsed.find("AMyActor").is_some());
        assert_eq!(doc.lines.line_count(), 5);
    }

    #[test]
    fn word_and_prefix_resolve_against_the_caret() {
        let doc = Document::parse(HEADER.to_string());
        // Line 1 (zero-based), column 6 is the start of `AMyActor`.
        let position = Position {
            line: 1,
            character: 8,
        };
        assert_eq!(doc.word_at(position), Some("AMyActor"));
        assert_eq!(doc.prefix_at(position), "AM");
    }

    #[test]
    fn store_round_trips_open_and_close() {
        let docs = Documents::new();
        assert!(docs.is_empty());

        docs.insert(uri(), Document::parse(HEADER.to_string()));
        assert!(docs.is_open(&uri()));
        assert_eq!(docs.with(&uri(), |d| d.parsed.symbols.len()).unwrap(), 1);

        assert!(docs.remove(&uri()).is_some());
        assert!(!docs.is_open(&uri()));
        assert!(docs.remove(&uri()).is_none());
    }

    #[test]
    fn insert_replaces_an_existing_buffer() {
        let docs = Documents::new();
        docs.insert(uri(), Document::parse(HEADER.to_string()));
        docs.insert(uri(), Document::parse(String::new()));
        assert_eq!(docs.len(), 1);
        assert_eq!(docs.with(&uri(), |d| d.parsed.symbols.len()).unwrap(), 0);
    }
}
