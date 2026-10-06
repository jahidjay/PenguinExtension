//! Request handling, kept free of transport concerns.
//!
//! Every function here takes plain data and returns plain LSP types. Database
//! access and the `tower-lsp` plumbing live in [`crate::server`], which means
//! these can be tested by calling them, with no runtime and no socket.

pub mod completion;
pub mod hover;
pub mod nav;

use tower_lsp::lsp_types::{CompletionItemKind, SymbolKind as LspSymbolKind};
use ue_parser::SymbolKind;

/// How a symbol should be iconified in an outline or symbol search.
pub fn lsp_symbol_kind(kind: SymbolKind) -> LspSymbolKind {
    match kind {
        SymbolKind::Class => LspSymbolKind::CLASS,
        SymbolKind::Struct => LspSymbolKind::STRUCT,
        SymbolKind::Interface => LspSymbolKind::INTERFACE,
        SymbolKind::Enum => LspSymbolKind::ENUM,
        SymbolKind::Function => LspSymbolKind::METHOD,
        SymbolKind::Property => LspSymbolKind::FIELD,
        // There is no delegate kind in LSP; an event is the closest reading of
        // what a multicast delegate is for.
        SymbolKind::Delegate => LspSymbolKind::EVENT,
    }
}

/// How a symbol should be iconified in a completion list.
pub fn lsp_completion_kind(kind: SymbolKind) -> CompletionItemKind {
    match kind {
        SymbolKind::Class => CompletionItemKind::CLASS,
        SymbolKind::Struct => CompletionItemKind::STRUCT,
        SymbolKind::Interface => CompletionItemKind::INTERFACE,
        SymbolKind::Enum => CompletionItemKind::ENUM,
        SymbolKind::Function => CompletionItemKind::METHOD,
        SymbolKind::Property => CompletionItemKind::FIELD,
        SymbolKind::Delegate => CompletionItemKind::EVENT,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_parser_kind_maps_to_both_lsp_vocabularies() {
        // `SymbolKind::ALL` is the parser's own list; if a kind is added there
        // the match arms above stop compiling, and this keeps the mapping
        // exercised rather than merely present.
        for kind in SymbolKind::ALL {
            let _ = lsp_symbol_kind(kind);
            let _ = lsp_completion_kind(kind);
        }
        assert_eq!(lsp_symbol_kind(SymbolKind::Property), LspSymbolKind::FIELD);
        assert_eq!(
            lsp_completion_kind(SymbolKind::Delegate),
            CompletionItemKind::EVENT
        );
    }
}
