//! Completion for reflected names and the macro argument currently being typed.
//!
//! Candidates arrive after the server overlays open buffers onto the index.
//! Member access deliberately returns no global suggestions: the model has no
//! owner information, so a global list would pretend to resolve a scope.

use super::lsp_completion_kind;
use crate::{catalog, context::macro_context_at, text, Document};
use std::collections::HashSet;
use tower_lsp::lsp_types::{
    CompletionItem, CompletionItemKind, CompletionTextEdit, Documentation, InsertTextFormat,
    Position, Range, TextEdit,
};
use ue_parser::{specifiers::split_top_level, UnrealSymbol};

/// Replaces the entire identifier, including the suffix when typing mid-word.
pub fn complete(
    document: &Document,
    position: Position,
    candidates: &[UnrealSymbol],
    limit: usize,
) -> Vec<CompletionItem> {
    let Some(offset) = document.offset(position) else {
        return Vec::new();
    };
    if limit == 0 {
        return Vec::new();
    }
    let Some(code) = code_at(&document.text, offset) else {
        return Vec::new();
    };
    let prefix = document.prefix_at(position).to_ascii_lowercase();
    let (_, start, end) = text::word_at(&document.text, offset).unwrap_or(("", offset, offset));
    let range = Range::new(
        document.lines.position(&document.text, start),
        document.lines.position(&document.text, end),
    );
    let mut items = Vec::new();
    if let Some(context) = macro_context_at(&code, offset) {
        if context.in_meta {
            return items;
        }
        let args_start = context.open_paren + 1;
        let before_key = split_top_level(&code[args_start..start], ',');
        // A value, or whitespace after a finished key, is not a new key slot.
        if !before_key.last().copied().unwrap_or("").trim().is_empty() {
            return items;
        }
        let args_end = argument_list_end(&code, context.open_paren);
        let mut entry_start = args_start;
        let mut used = HashSet::new();
        for entry in split_top_level(&code[args_start..args_end], ',') {
            let next = entry_start + entry.len() + 1;
            // The key being replaced is not a duplicate of itself. Include
            // other entries on BOTH sides of the caret, not just the prefix.
            if !(entry_start <= offset && offset < next) {
                used.extend(
                    ue_parser::parse_specifiers(entry)
                        .into_iter()
                        .map(|spec| spec.key.to_ascii_lowercase()),
                );
            }
            entry_start = next;
        }
        for spec in catalog::specifiers_for(context.macro_name).unwrap_or_default() {
            let name = spec.name.to_ascii_lowercase();
            if !name.starts_with(&prefix) || used.contains(&name) {
                continue;
            }
            let insertion = if code[end..].trim_start().starts_with('=') {
                spec.name
            } else {
                spec.insertion()
            };
            let mut item = replacement(spec.name, insertion, CompletionItemKind::KEYWORD, range);
            item.detail = Some(context.macro_name.to_string());
            item.documentation = Some(Documentation::String(spec.description.to_string()));
            items.push(item);
        }
    } else {
        let before = code[..start].trim_end();
        if before.ends_with('.') || before.ends_with("->") || before.ends_with("::") {
            return items;
        }
        let mut seen = HashSet::new();
        for symbol in document.parsed.symbols.iter().chain(candidates) {
            if !symbol.name.to_ascii_lowercase().starts_with(&prefix)
                || !seen.insert((symbol.name.clone(), symbol.kind))
            {
                continue;
            }
            let mut item = replacement(
                &symbol.name,
                &symbol.name,
                lsp_completion_kind(symbol.kind),
                range,
            );
            item.detail = Some(match symbol.type_name.as_deref() {
                Some(ty) => format!("{} · {} · {}", ty, symbol.kind.as_str(), symbol.macro_name),
                None => format!("{} · {}", symbol.kind.as_str(), symbol.macro_name),
            });
            items.push(item);
        }
        for name in catalog::MACRO_NAMES {
            if name.to_ascii_lowercase().starts_with(&prefix) {
                items.push(replacement(name, name, CompletionItemKind::KEYWORD, range));
            }
        }
    }
    items.sort_by(|a, b| {
        a.label
            .to_ascii_lowercase()
            .cmp(&b.label.to_ascii_lowercase())
            .then_with(|| a.label.cmp(&b.label))
    });
    items.truncate(limit);
    items
}

fn replacement(
    name: &str,
    insertion: &str,
    kind: CompletionItemKind,
    range: Range,
) -> CompletionItem {
    CompletionItem {
        label: name.to_string(),
        kind: Some(kind),
        insert_text_format: Some(InsertTextFormat::PLAIN_TEXT),
        text_edit: Some(CompletionTextEdit::Edit(TextEdit::new(
            range,
            insertion.to_string(),
        ))),
        ..CompletionItem::default()
    }
}

/// End of the enclosing argument list in text with comments/literals masked.
fn argument_list_end(code: &str, open: usize) -> usize {
    let mut depth = 0;
    for (i, byte) in code.bytes().enumerate().skip(open) {
        match byte {
            b'(' => depth += 1,
            b')' => {
                depth -= 1;
                if depth == 0 {
                    return i;
                }
            }
            _ => {}
        }
    }
    code.len()
}

/// Mask comments/literals without changing byte offsets, or reject a caret
/// inside one. This also keeps their punctuation out of macro/scope detection.
fn code_at(source: &str, offset: usize) -> Option<String> {
    let bytes = source.as_bytes();
    let mut code = bytes.to_vec();
    let mut i = 0;
    while i < bytes.len() {
        // Apostrophes in preprocessing numbers are digit separators, not the
        // beginning of character literals (including hex and exponent forms).
        if bytes[i].is_ascii_digit()
            && (i == 0 || !(bytes[i - 1].is_ascii_alphanumeric() || bytes[i - 1] == b'_'))
        {
            i += 1;
            while i < bytes.len() {
                if bytes[i].is_ascii_alphanumeric()
                    || matches!(bytes[i], b'_' | b'.')
                    || (bytes[i] == b'\''
                        && bytes
                            .get(i + 1)
                            .is_some_and(|b| b.is_ascii_alphanumeric() || *b == b'_'))
                    || (matches!(bytes[i], b'+' | b'-')
                        && matches!(bytes[i - 1], b'e' | b'E' | b'p' | b'P'))
                {
                    i += 1;
                } else {
                    break;
                }
            }
            continue;
        }
        let line_comment = bytes[i..].starts_with(b"//");
        let end = if line_comment {
            let mut j = i + 2;
            loop {
                let Some(n) = source[j..].find('\n') else {
                    break source.len() + 1;
                };
                let newline = j + n;
                let before_newline = if bytes[newline - 1] == b'\r' {
                    newline - 1
                } else {
                    newline
                };
                if before_newline > 0 && bytes[before_newline - 1] == b'\\' {
                    j = newline + 1;
                } else {
                    break newline;
                }
            }
        } else if bytes[i..].starts_with(b"/*") {
            source[i + 2..]
                .find("*/")
                .map(|n| i + n + 4)
                .unwrap_or(source.len() + 1)
        } else if bytes[i..].starts_with(b"R\"") {
            let rest = &source[i + 2..];
            if let Some(open) = rest.find('(').filter(|n| *n <= 16) {
                let close = format!("){}\"", &rest[..open]);
                let body = i + 3 + open;
                source[body..]
                    .find(&close)
                    .map(|n| body + n + close.len())
                    .unwrap_or(source.len() + 1)
            } else {
                i += 1;
                continue;
            }
        } else if bytes[i] == b'"' || bytes[i] == b'\'' {
            let quote = bytes[i];
            let mut j = i + 1;
            while j < bytes.len() {
                if bytes[j] == b'\\' {
                    j += 2;
                } else if bytes[j] == quote {
                    break;
                } else {
                    j += 1;
                }
            }
            j + 1
        } else {
            i += 1;
            continue;
        };
        // A caret just before a line-comment newline still inserts into the
        // comment; a caret after */ or a closing quote is back in code.
        if i < offset && (offset < end || (line_comment && offset == end)) {
            return None;
        }
        for byte in &mut code[i..end.min(bytes.len())] {
            if !matches!(*byte, b'\r' | b'\n') {
                *byte = b' ';
            }
        }
        i = end;
    }
    // Every replaced span is bounded by ASCII delimiters (or EOF).
    Some(String::from_utf8(code).expect("masking preserves UTF-8"))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn at(marked: &str) -> (Document, Position) {
        let offset = marked.find('|').unwrap();
        let doc = Document::parse(marked.replacen('|', "", 1));
        let position = doc.lines.position(&doc.text, offset);
        (doc, position)
    }

    fn labels(marked: &str) -> Vec<String> {
        let (doc, pos) = at(marked);
        complete(&doc, pos, &[], 100)
            .into_iter()
            .map(|item| item.label)
            .collect()
    }

    #[test]
    fn half_typed_specifiers_are_macro_specific_and_ignore_case() {
        assert_eq!(
            labels("UPROPERTY(edit|"),
            ["EditAnywhere", "EditDefaultsOnly", "EditInstanceOnly"]
        );
        assert_eq!(labels("UFUNCTION(Ser|"), ["Server"]);
        assert!(labels("USTRUCT(Ser|").is_empty());
    }

    #[test]
    fn already_supplied_specifiers_are_not_suggested_twice() {
        assert_eq!(
            labels("UPROPERTY(EditAnywhere, Edit|"),
            ["EditDefaultsOnly", "EditInstanceOnly"]
        );
        assert_eq!(
            labels("UPROPERTY(meta=(ClampMin=\"0.0)\"), Edit|"),
            ["EditAnywhere", "EditDefaultsOnly", "EditInstanceOnly"]
        );
    }

    #[test]
    fn value_scaffolds_are_plain_text_and_do_not_duplicate_assignments() {
        for (source, expected) in [
            ("UPROPERTY(Cat|", "Category = \"\""),
            ("UPROPERTY(Cat|egory = \"Stats\")", "Category"),
        ] {
            let (doc, pos) = at(source);
            let items = complete(&doc, pos, &[], 100);
            assert_eq!(items.len(), 1);
            let Some(CompletionTextEdit::Edit(edit)) = &items[0].text_edit else {
                panic!("missing edit")
            };
            assert_eq!(edit.new_text, expected);
            assert_eq!(
                items[0].insert_text_format,
                Some(InsertTextFormat::PLAIN_TEXT)
            );
        }
    }

    #[test]
    fn metadata_values_comments_and_literals_do_not_offer_global_symbols() {
        for source in [
            "UPROPERTY(meta=(Edit|",
            "UPROPERTY(Category=Foo|",
            "// UPROPERTY(Edit|",
            "/* UPROPERTY(Edit|",
            "const char* s = \"UPROPERTY(Edit|",
            "auto s = R\"tag(UPROPERTY(Edit|)tag\";",
        ] {
            assert!(labels(source).is_empty(), "{source}");
        }
    }

    #[test]
    fn member_access_does_not_masquerade_as_scope_resolution() {
        for source in ["Object.Heal|", "Object->Heal|", "AActor::Heal|"] {
            let (doc, pos) = at(source);
            let symbols = Document::parse("UCLASS() class Health {};".into())
                .parsed
                .symbols;
            assert!(complete(&doc, pos, &symbols, 100).is_empty());
        }
    }

    #[test]
    fn local_symbols_win_duplicate_candidates_and_limits_are_honored() {
        let (doc, pos) = at("UCLASS() class ALocal {};\nAL|");
        let mut indexed = doc.parsed.symbols[0].clone();
        indexed.macro_name = "STALE".into();
        let items = complete(&doc, pos, &[indexed], 1);
        assert_eq!(items.len(), 1);
        assert_eq!(items[0].label, "ALocal");
        assert!(!items[0].detail.as_ref().unwrap().contains("STALE"));
        assert!(complete(&doc, pos, &[], 0).is_empty());
        assert!(complete(&doc, Position::new(99, 0), &[], 20).is_empty());
    }

    #[test]
    fn mid_word_edits_replace_suffix_and_use_utf16_columns() {
        let (doc, pos) = at("/*🐧*/ UPROPERTY(Edit|Anywhere)");
        let items = complete(&doc, pos, &[], 100);
        let Some(CompletionTextEdit::Edit(edit)) = &items[0].text_edit else {
            panic!("missing edit")
        };
        assert_eq!(
            edit.range,
            Range::new(Position::new(0, 17), Position::new(0, 29))
        );
    }

    #[test]
    fn macros_inside_completed_comments_do_not_leak_into_following_code() {
        assert!(labels("/* UPROPERTY( */ Edit|").is_empty());
        assert!(labels("// UPROPERTY(\nEdit|").is_empty());
    }

    #[test]
    fn macro_names_are_available_in_ordinary_code() {
        assert_eq!(labels("UPRO|"), ["UPROPERTY"]);
    }

    #[test]
    fn line_comment_end_and_continuations_remain_comments() {
        for source in [
            "// UPRO|\n",
            "// UPRO|\r\n",
            "// continued \\\nUPRO|\n",
            "// continued \\\r\nUPRO|\r\n",
        ] {
            assert!(labels(source).is_empty(), "{source:?}");
        }
        assert_eq!(labels("// done\nUPRO|"), ["UPROPERTY"]);
    }

    #[test]
    fn character_literals_and_numeric_separators_are_distinct() {
        for source in [
            "auto n = 1'000; UPRO|",
            "auto n = 0xFF'FF; UPRO|",
            "auto n = 1.0e+1'0; UPRO|",
        ] {
            assert_eq!(labels(source), ["UPROPERTY"], "{source}");
        }
        for source in ["auto c = 'UPRO|';", "auto c = L'UPRO|';"] {
            assert!(labels(source).is_empty(), "{source}");
        }
    }

    #[test]
    fn raw_literal_quotes_and_parentheses_do_not_affect_following_code() {
        for source in [
            "auto s = u8R\"tag(a \" UPRO| )tag\";",
            "auto s = LR\"tag(a \\\" UPRO| )tag\";",
            "auto s = R\"(a \" UPRO| )\";",
            "auto s = R\"tag(unterminated \" UPRO|",
        ] {
            assert!(labels(source).is_empty(), "{source}");
        }
        assert_eq!(labels("auto s = R\"tag(a \" )tag\"; UPRO|"), ["UPROPERTY"]);
    }

    #[test]
    fn duplicate_specifiers_after_the_caret_are_excluded_but_current_key_is_not() {
        assert_eq!(
            labels("UPROPERTY(Edit|, EditAnywhere, EditDefaultsOnly)"),
            ["EditInstanceOnly"]
        );
        assert_eq!(
            labels("UPROPERTY(Edit|Anywhere, EditDefaultsOnly)"),
            ["EditAnywhere", "EditInstanceOnly"]
        );
        assert_eq!(
            labels("UPROPERTY(Edit|)\nUPROPERTY(EditAnywhere)"),
            ["EditAnywhere", "EditDefaultsOnly", "EditInstanceOnly"]
        );
    }

    #[test]
    fn comments_are_whitespace_for_macro_keys_and_scope_guards() {
        assert_eq!(
            labels("UPROPERTY(/* ) ; */ Edit|, /* already set */ EditAnywhere)"),
            ["EditDefaultsOnly", "EditInstanceOnly"]
        );
        for source in [
            "Object. /* note */ UPRO|",
            "Object-> /* note */ UPRO|",
            "AActor:: /* note */ UPRO|",
        ] {
            assert!(labels(source).is_empty(), "{source}");
        }
        let (doc, pos) = at("UPROPERTY(Cat|egory /* note */ = \"Stats\")");
        let items = complete(&doc, pos, &[], 100);
        let Some(CompletionTextEdit::Edit(edit)) = &items[0].text_edit else {
            panic!("missing edit")
        };
        assert_eq!(edit.new_text, "Category");
    }

    #[test]
    fn a_completed_key_followed_by_space_is_not_a_new_specifier_slot() {
        assert!(labels("UPROPERTY(EditAnywhere |)").is_empty());
        assert!(labels("UPROPERTY(EditAnywhere /* note */ |)").is_empty());
        assert!(labels("UPROPERTY(Category | = \"Stats\")").is_empty());
        assert_eq!(
            labels("UPROPERTY(EditAnywhere, Edit|)"),
            ["EditDefaultsOnly", "EditInstanceOnly"]
        );
    }

    #[test]
    fn whitespace_and_punctuation_edits_do_not_consume_adjacent_identifiers() {
        for source in [
            "/*🐧*/ Name |",
            "/*🐧*/ Name;|",
            "/*🐧*/ Name(|",
            "/*🐧*/ Name,|",
        ] {
            let (doc, pos) = at(source);
            let items = complete(&doc, pos, &[], 100);
            assert!(!items.is_empty());
            for item in items {
                let Some(CompletionTextEdit::Edit(edit)) = item.text_edit else {
                    panic!("missing edit")
                };
                assert_eq!(edit.range, Range::new(pos, pos), "{source}");
            }
        }
        let (doc, pos) = at("/*🐧*/ UPRO|PERTY;");
        let items = complete(&doc, pos, &[], 100);
        let Some(CompletionTextEdit::Edit(edit)) = &items[0].text_edit else {
            panic!("missing edit")
        };
        assert_eq!(
            edit.range,
            Range::new(Position::new(0, 7), Position::new(0, 16))
        );
    }
}
