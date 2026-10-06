//! Definition navigation and flat symbol lists, without filesystem or DB I/O.
//!
//! The parser records the reflection macro's range, not the declaration name or
//! body. Local results deliberately use that known anchor rather than guessing
//! at a later occurrence of the name. Indexed results have no source text, so
//! only their one-based macro line can be converted to an LSP location.

use std::collections::HashSet;

use tower_lsp::lsp_types::{DocumentSymbol, Location, Position, Range, SymbolInformation, Url};
use ue_db::StoredSymbol;
use ue_parser::UnrealSymbol;

use super::lsp_symbol_kind;
use crate::Document;

/// Every exact-name match for the identifier at `position`, local matches first.
///
/// The open buffer replaces the indexed copy of its entire file, including
/// declarations deleted by unsaved edits. Other files remain candidates even
/// when there is a local match: without owner/scope information we cannot choose
/// between identically named members or declarations in different modules.
pub fn definitions(
    uri: &Url,
    document: &Document,
    position: Position,
    indexed: &[StoredSymbol],
) -> Vec<Location> {
    let Some(name) = document.word_at(position) else {
        return Vec::new();
    };
    let mut seen = HashSet::new();
    let mut locations = Vec::new();

    for symbol in document.parsed.symbols.iter().filter(|s| s.name == name) {
        let range = local_range(document, symbol);
        if seen.insert((uri.clone(), range_key(range))) {
            locations.push(Location {
                uri: uri.clone(),
                range,
            });
        }
    }

    for stored in indexed.iter().filter(|s| s.symbol.name == name) {
        let Some(location) = indexed_location(stored) else {
            continue;
        };
        if same_file(uri, &location.uri) {
            continue;
        }
        if seen.insert((location.uri.clone(), range_key(location.range))) {
            locations.push(location);
        }
    }
    locations
}

/// A source-ordered, flat outline of reflected symbols in the live buffer.
///
/// Both ranges identify the reflection macro, not a fabricated declaration
/// extent. No children are inferred: the parser model does not carry ownership.
#[allow(deprecated)] // lsp-types still requires the superseded field in literals.
pub fn document_symbols(document: &Document) -> Vec<DocumentSymbol> {
    let mut seen = HashSet::new();
    document
        .parsed
        .symbols
        .iter()
        .filter(|symbol| !symbol.name.is_empty())
        .filter_map(|symbol| {
            let range = local_range(document, symbol);
            if !seen.insert((symbol.name.as_str(), symbol.kind, range_key(range))) {
                return None;
            }
            Some(DocumentSymbol {
                name: symbol.name.clone(),
                detail: symbol.type_name.clone(),
                kind: lsp_symbol_kind(symbol.kind),
                tags: None,
                deprecated: None,
                range,
                selection_range: range,
                children: None,
            })
        })
        .collect()
}

/// Case-insensitive substring search, preserving the supplied index order.
///
/// An empty query browses all names up to `limit`. Invalid paths and duplicate
/// name/kind/locations do not consume that limit; equal names in different files
/// remain separate results. The caller supplies the indexed snapshot to search.
#[allow(deprecated)] // lsp-types still requires the superseded field in literals.
pub fn workspace_symbols(
    query: &str,
    indexed: &[StoredSymbol],
    limit: usize,
) -> Vec<SymbolInformation> {
    if limit == 0 {
        return Vec::new();
    }
    let query = query.to_lowercase();
    let mut seen = HashSet::new();
    indexed
        .iter()
        .filter(|stored| {
            !stored.symbol.name.is_empty() && stored.symbol.name.to_lowercase().contains(&query)
        })
        .filter_map(|stored| {
            let location = indexed_location(stored)?;
            let symbol = &stored.symbol;
            if !seen.insert((
                symbol.name.as_str(),
                symbol.kind,
                location.uri.clone(),
                range_key(location.range),
            )) {
                return None;
            }
            Some(SymbolInformation {
                name: symbol.name.clone(),
                kind: lsp_symbol_kind(symbol.kind),
                tags: None,
                deprecated: None,
                location,
                container_name: None,
            })
        })
        .take(limit)
        .collect()
}

/// Convert only validated byte boundaries through the document's LineIndex.
/// A malformed/unavailable range degrades to the recorded macro's source line.
fn local_range(document: &Document, symbol: &UnrealSymbol) -> Range {
    if document
        .text
        .get(symbol.byte_range.clone())
        .is_some_and(|span| !span.is_empty())
    {
        Range {
            start: document
                .lines
                .position(&document.text, symbol.byte_range.start),
            end: document
                .lines
                .position(&document.text, symbol.byte_range.end),
        }
    } else {
        document.lines.line_as_range(&document.text, symbol.line)
    }
}

fn indexed_location(stored: &StoredSymbol) -> Option<Location> {
    // A NUL cannot belong to a filesystem path, even though a URL can encode it.
    if stored.file.contains('\0') {
        return None;
    }
    let uri = Url::from_file_path(&stored.file).ok()?;
    let position = Position {
        line: stored.symbol.line.saturating_sub(1).min(u32::MAX as usize) as u32,
        character: 0,
    };
    Some(Location {
        uri,
        range: Range {
            start: position,
            end: position,
        },
    })
}

fn same_file(left: &Url, right: &Url) -> bool {
    if left == right {
        return true;
    }
    let (Ok(left), Ok(right)) = (left.to_file_path(), right.to_file_path()) else {
        return false;
    };
    let left = ue_db::path_key(&left);
    let right = ue_db::path_key(&right);
    if cfg!(windows) {
        left.eq_ignore_ascii_case(&right)
    } else {
        left == right
    }
}

// LSP ranges themselves do not implement Hash.
fn range_key(range: Range) -> (u32, u32, u32, u32) {
    (
        range.start.line,
        range.start.character,
        range.end.line,
        range.end.character,
    )
}

#[cfg(test)]
mod tests {
    use std::path::Path;

    use tower_lsp::lsp_types::SymbolKind as LspSymbolKind;
    use ue_parser::SymbolKind;

    use super::*;

    fn uri(file: &str) -> Url {
        let root = if cfg!(windows) {
            "C:/Project/Source"
        } else {
            "/project/Source"
        };
        Url::from_file_path(Path::new(root).join(file)).unwrap()
    }

    fn stored(uri: &Url, name: &str, kind: SymbolKind, line: usize) -> StoredSymbol {
        StoredSymbol {
            file: ue_db::path_key(&uri.to_file_path().unwrap()),
            symbol: UnrealSymbol {
                name: name.to_string(),
                kind,
                macro_name: "UCLASS".to_string(),
                specifiers: Vec::new(),
                type_name: None,
                bases: Vec::new(),
                line,
                // These offsets cannot be interpreted without the indexed source.
                byte_range: 1000..2000,
            },
        }
    }

    fn range(start_line: u32, start_col: u32, end_line: u32, end_col: u32) -> Range {
        Range {
            start: Position {
                line: start_line,
                character: start_col,
            },
            end: Position {
                line: end_line,
                character: end_col,
            },
        }
    }

    fn reference(document: &Document, name: &str) -> Position {
        document
            .lines
            .position(&document.text, document.text.rfind(name).unwrap())
    }

    #[test]
    fn outline_is_flat_source_ordered_and_covers_every_kind() {
        let document = Document::parse(
            concat!(
                "DECLARE_DYNAMIC_MULTICAST_DELEGATE(FChanged);\n",
                "UENUM()\nenum class EState { Idle };\n",
                "USTRUCT()\nstruct FStats {};\n",
                "UINTERFACE()\nclass UInteract {};\n",
                "UCLASS()\nclass AHero {\n",
                "    UPROPERTY()\n    float Health;\n",
                "    UFUNCTION()\n    void Heal();\n",
                "};\n",
            )
            .to_string(),
        );
        let outline = document_symbols(&document);
        let names_and_kinds: Vec<_> = outline.iter().map(|s| (s.name.as_str(), s.kind)).collect();
        assert_eq!(
            names_and_kinds,
            vec![
                ("FChanged", LspSymbolKind::EVENT),
                ("EState", LspSymbolKind::ENUM),
                ("FStats", LspSymbolKind::STRUCT),
                ("UInteract", LspSymbolKind::INTERFACE),
                ("AHero", LspSymbolKind::CLASS),
                ("Health", LspSymbolKind::FIELD),
                ("Heal", LspSymbolKind::METHOD),
            ]
        );
        assert_eq!(outline[4].range, range(7, 0, 7, 8));
        assert_eq!(outline[5].range, range(9, 4, 9, 15));
        assert_eq!(outline[5].detail.as_deref(), Some("float"));
        assert_eq!(outline[6].detail.as_deref(), Some("void"));
        for (item, parsed) in outline.iter().zip(&document.parsed.symbols) {
            assert!(item.children.is_none());
            assert_eq!(item.selection_range, item.range);
            let start = document.offset(item.range.start).unwrap();
            let end = document.offset(item.range.end).unwrap();
            assert_eq!(start..end, parsed.byte_range);
            assert!(document.text[start..end].starts_with(&parsed.macro_name));
        }
    }

    #[test]
    fn multiline_macro_is_not_mistaken_for_the_declaration_or_its_name() {
        let document = Document::parse(
            concat!(
                "UCLASS(\n    meta = (DisplayName = \"AHero\")\n)\n",
                "class AHero {};\nAHero* Hero;\n",
            )
            .to_string(),
        );
        let expected = range(0, 0, 2, 1);
        let outline = document_symbols(&document);
        assert_eq!(outline.len(), 1);
        assert_eq!(outline[0].range, expected);
        assert_eq!(outline[0].selection_range, expected);
        assert_eq!(
            definitions(
                &uri("Hero.h"),
                &document,
                reference(&document, "AHero"),
                &[]
            ),
            vec![Location {
                uri: uri("Hero.h"),
                range: expected
            }]
        );
    }

    #[test]
    fn local_definitions_use_live_utf16_macro_coordinates() {
        let document = Document::parse(
            "/* \u{1F427} */ UCLASS()\nclass AHero {};\n/* \u{1F427} */ AHero* Hero;\n".to_string(),
        );
        let caret = Position {
            line: 2,
            character: 12,
        };
        let expected = range(0, 9, 0, 17);
        assert_eq!(
            definitions(&uri("Hero.h"), &document, caret, &[]),
            vec![Location {
                uri: uri("Hero.h"),
                range: expected
            }]
        );
        assert_eq!(document_symbols(&document)[0].range, expected);
        // A caret just past the identifier still resolves it.
        assert_eq!(
            definitions(
                &uri("Hero.h"),
                &document,
                Position {
                    line: 2,
                    character: 14
                },
                &[]
            )
            .len(),
            1
        );
    }

    #[test]
    fn macro_contents_convert_the_end_column_to_utf16_too() {
        let document = Document::parse(
            "UCLASS(meta=(DisplayName=\"\u{1F427}\"))\nclass AHero {};".to_string(),
        );
        let outline = document_symbols(&document);
        assert_eq!(outline.len(), 1);
        let expected_end = document.text.lines().next().unwrap().encode_utf16().count() as u32;
        assert_eq!(outline[0].range, range(0, 0, 0, expected_end));
    }

    #[test]
    fn indexed_fallback_is_a_zero_based_macro_line_at_column_zero() {
        let document = Document::parse("AHero* Hero;".to_string());
        let indexed = vec![stored(&uri("Other #1.h"), "AHero", SymbolKind::Class, 17)];
        let locations = definitions(&uri("Current.h"), &document, Position::default(), &indexed);
        assert_eq!(
            locations,
            vec![Location {
                uri: uri("Other #1.h"),
                range: range(16, 0, 16, 0)
            }]
        );
        assert!(locations[0].uri.as_str().contains("Other%20%231.h"));
    }

    #[test]
    fn locals_precede_distinct_indexed_definitions_without_guessing_an_owner() {
        let mut document = Document::parse(
            concat!(
                "UCLASS()\nclass AHero {};\n",
                "UCLASS()\nclass AHero {};\nAHero* Hero;",
            )
            .to_string(),
        );
        document
            .parsed
            .symbols
            .push(document.parsed.symbols[0].clone());
        let first = stored(&uri("One.h"), "AHero", SymbolKind::Class, 4);
        let second = stored(&uri("Two.h"), "AHero", SymbolKind::Class, 4);
        let indexed = vec![first.clone(), first, second];
        let locations = definitions(
            &uri("Current.h"),
            &document,
            reference(&document, "AHero"),
            &indexed,
        );
        assert_eq!(locations.len(), 4);
        assert_eq!(
            locations[0],
            Location {
                uri: uri("Current.h"),
                range: range(0, 0, 0, 8)
            }
        );
        assert_eq!(
            locations[1],
            Location {
                uri: uri("Current.h"),
                range: range(2, 0, 2, 8)
            }
        );
        assert_eq!(locations[2].uri, uri("One.h"));
        assert_eq!(locations[3].uri, uri("Two.h"));
    }

    #[test]
    fn unsaved_deletions_and_moves_suppress_the_entire_stale_current_file() {
        let current = uri("Current #1.h");
        let indexed = vec![
            stored(&current, "AHero", SymbolKind::Class, 80),
            stored(&current, "Deleted", SymbolKind::Class, 90),
            stored(&uri("Other.h"), "Deleted", SymbolKind::Class, 6),
        ];
        let document =
            Document::parse("UCLASS()\nclass AHero {};\nAHero* Hero;\nDeleted* Old;".to_string());
        let moved = definitions(&current, &document, reference(&document, "AHero"), &indexed);
        assert_eq!(
            moved,
            vec![Location {
                uri: current.clone(),
                range: range(0, 0, 0, 8)
            }]
        );
        let deleted = definitions(
            &current,
            &document,
            reference(&document, "Deleted"),
            &indexed,
        );
        assert_eq!(
            deleted,
            vec![Location {
                uri: uri("Other.h"),
                range: range(5, 0, 5, 0)
            }]
        );
        let no_declarations = Document::parse("AHero* Hero;".to_string());
        assert!(definitions(&current, &no_declarations, Position::default(), &indexed).is_empty());
    }

    #[cfg(windows)]
    #[test]
    fn stale_windows_paths_match_across_slashes_and_case() {
        let current = uri("Current.h");
        let mut old = stored(&current, "AHero", SymbolKind::Class, 10);
        old.file = old.file.to_ascii_uppercase().replace('/', "\\");
        let document = Document::parse("AHero* Hero;".to_string());
        assert!(definitions(&current, &document, Position::default(), &[old]).is_empty());
    }

    #[test]
    fn invalid_and_non_file_stored_paths_are_skipped() {
        let document = Document::parse("AHero* Hero;".to_string());
        let valid = stored(&uri("Valid.h"), "AHero", SymbolKind::Class, 2);
        let bad_paths = [
            "".to_string(),
            "relative/Header.h".to_string(),
            "https://example.com/Header.h".to_string(),
            "untitled:Header.h".to_string(),
            uri("NotAPath.h").to_string(),
            format!("{}\0.h", valid.file),
        ];
        let mut indexed: Vec<_> = bad_paths
            .into_iter()
            .map(|file| {
                let mut symbol = valid.clone();
                symbol.file = file;
                symbol
            })
            .collect();
        indexed.push(valid);
        let locations = definitions(&uri("Current.h"), &document, Position::default(), &indexed);
        assert_eq!(locations.len(), 1);
        assert_eq!(locations[0].uri, uri("Valid.h"));
        let workspace = workspace_symbols("hero", &indexed, 1);
        assert_eq!(workspace.len(), 1);
        assert_eq!(workspace[0].location.uri, uri("Valid.h"));
    }

    #[test]
    fn non_file_open_buffers_still_have_local_definitions() {
        let current = Url::parse("untitled:Scratch.h").unwrap();
        let document = Document::parse("UCLASS()\nclass AHero {};\nAHero* Hero;".to_string());
        let indexed = vec![stored(&uri("Other.h"), "AHero", SymbolKind::Class, 3)];
        let locations = definitions(&current, &document, reference(&document, "AHero"), &indexed);
        assert_eq!(locations.len(), 2);
        assert_eq!(locations[0].uri, current);
        assert_eq!(locations[1].uri, uri("Other.h"));
    }

    #[test]
    fn workspace_search_is_literal_case_insensitive_and_bounded_after_dedup() {
        let first = stored(&uri("One.h"), "On_HealthChanged", SymbolKind::Delegate, 3);
        let indexed = vec![
            stored(&uri("None.h"), "Unrelated", SymbolKind::Property, 1),
            first.clone(),
            first,
            stored(&uri("Two.h"), "GetHealth", SymbolKind::Function, 5),
            stored(&uri("Three.h"), "Health", SymbolKind::Property, 7),
        ];
        let matches = workspace_symbols("hEaLtH", &indexed, 2);
        assert_eq!(matches.len(), 2);
        assert_eq!(matches[0].name, "On_HealthChanged");
        assert_eq!(matches[0].kind, LspSymbolKind::EVENT);
        assert_eq!(matches[0].location.range, range(2, 0, 2, 0));
        assert_eq!(matches[1].name, "GetHealth");
        assert_eq!(matches[1].kind, LspSymbolKind::METHOD);
        assert!(matches.iter().all(|s| s.container_name.is_none()));
        assert_eq!(workspace_symbols("_", &indexed, 10).len(), 1);
        assert!(workspace_symbols("%", &indexed, 10).is_empty());
        assert_eq!(workspace_symbols("", &indexed, 2).len(), 2);
        assert!(workspace_symbols("health", &indexed, 0).is_empty());
    }

    #[test]
    fn workspace_dedup_keeps_different_names_kinds_and_locations() {
        let first = stored(&uri("One.h"), "Shared", SymbolKind::Class, 1);
        let indexed = vec![
            first.clone(),
            first,
            stored(&uri("One.h"), "Shared", SymbolKind::Struct, 1),
            stored(&uri("One.h"), "Other", SymbolKind::Class, 1),
            stored(&uri("One.h"), "Shared", SymbolKind::Class, 2),
            stored(&uri("Two.h"), "Shared", SymbolKind::Class, 1),
        ];
        assert_eq!(workspace_symbols("", &indexed, 20).len(), 5);
    }

    #[test]
    fn outline_dedup_keeps_different_names_kinds_and_locations() {
        let mut document =
            Document::parse("UCLASS()\nclass AHero {};\nUCLASS()\nclass AHero {};".to_string());
        let original = document.parsed.symbols[0].clone();
        document.parsed.symbols.push(original.clone());
        let mut renamed = original.clone();
        renamed.name = "Other".to_string();
        document.parsed.symbols.push(renamed);
        let mut another_kind = original;
        another_kind.kind = SymbolKind::Struct;
        document.parsed.symbols.push(another_kind);
        assert_eq!(document_symbols(&document).len(), 4);
    }

    #[test]
    fn bad_local_ranges_fall_back_to_the_macro_line_without_panicking() {
        let mut document = Document::parse("/* \u{1F427} */ UCLASS()\nclass AHero {};".to_string());
        for byte_range in [
            0..0,
            999..1000,
            std::ops::Range {
                start: 1000,
                end: 999,
            },
            4..5,
        ] {
            document.parsed.symbols[0].byte_range = byte_range;
            assert_eq!(document_symbols(&document)[0].range, range(0, 0, 0, 17));
        }
        document.parsed.symbols[0].line = 999;
        assert_eq!(document_symbols(&document)[0].range, Range::default());
    }

    #[test]
    fn indexed_line_numbers_saturate_instead_of_underflowing_or_wrapping() {
        let indexed = vec![
            stored(&uri("Zero.h"), "Zero", SymbolKind::Class, 0),
            stored(&uri("Huge.h"), "Huge", SymbolKind::Class, usize::MAX),
        ];
        let matches = workspace_symbols("", &indexed, 10);
        assert_eq!(matches[0].location.range, Range::default());
        let last_line = usize::MAX.saturating_sub(1).min(u32::MAX as usize) as u32;
        assert_eq!(matches[1].location.range, range(last_line, 0, last_line, 0));
    }

    #[test]
    fn missing_case_mismatched_or_empty_inputs_produce_no_results() {
        let current = uri("Current.h");
        let empty = Document::default();
        assert!(document_symbols(&empty).is_empty());
        assert!(definitions(&current, &empty, Position::default(), &[]).is_empty());
        assert!(workspace_symbols("", &[], 10).is_empty());
        assert!(workspace_symbols("AHero", &[], 10).is_empty());

        let document = Document::parse("ahero* Hero;\n   \nMissing;".to_string());
        let indexed = vec![stored(&uri("Other.h"), "AHero", SymbolKind::Class, 1)];
        for position in [
            Position::default(),
            Position {
                line: 1,
                character: 1,
            },
            Position {
                line: 2,
                character: 2,
            },
            Position {
                line: 99,
                character: 0,
            },
        ] {
            assert!(definitions(&current, &document, position, &indexed).is_empty());
        }
    }
}
