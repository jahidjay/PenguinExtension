//! Hover facts without inventing C++ declarations.
//!
//! The parser records names, types and macro metadata, not complete signatures,
//! documentation or owners. Show those recorded facts separately rather than
//! reconstructing a declaration. The open buffer wins over server-selected
//! candidates, and a recognized macro context never falls back to symbol names.
//! Source-controlled fields are literal code with longer backtick delimiters
//! than their contents, so Markdown, HTML and multiline values stay inert.

use tower_lsp::lsp_types::{Hover, HoverContents, MarkupContent, MarkupKind, Position, Range};
use ue_parser::{SymbolKind, UnrealSymbol};

use crate::{catalog, context::macro_context_at, docs::Document, text::word_at};

/// Describes the full identifier under or immediately before the caret.
///
/// `candidates` have already been filtered and overlaid by the caller; this
/// handler performs no workspace lookup, parsing or I/O. Exact local names take
/// precedence, otherwise the first matching candidate is used.
pub fn hover(
    document: &Document,
    position: Position,
    candidates: &[UnrealSymbol],
) -> Option<Hover> {
    let offset = document.offset(position)?;
    let (word, start, end) = word_at(&document.text, offset)?;

    let value = if let Some(context) = macro_context_at(&document.text, offset) {
        // Metadata keys have their own grammar, which the ordinary specifier
        // catalog does not describe. Unknown specifiers must not resolve to a
        // coincidentally named symbol either.
        if context.in_meta {
            return None;
        }
        let specifier = catalog::lookup(context.macro_name, word)?;
        format!(
            "{} specifier for {}\n\n{}",
            code(specifier.name),
            code(context.macro_name),
            specifier.description
        )
    } else {
        let symbol = document
            .parsed
            .find(word)
            .or_else(|| candidates.iter().find(|symbol| symbol.name == word))?;
        symbol_markdown(symbol)
    };

    Some(Hover {
        contents: HoverContents::Markup(MarkupContent {
            kind: MarkupKind::Markdown,
            value,
        }),
        // Symbol byte ranges describe the declaring macro, not the identifier
        // at this use site. Convert word_at's byte range through the buffer's
        // LineIndex so astral characters before the word count as two units.
        range: Some(Range {
            start: document.lines.position(&document.text, start),
            end: document.lines.position(&document.text, end),
        }),
    })
}

fn symbol_markdown(symbol: &UnrealSymbol) -> String {
    let mut markdown = format!(
        "{} ({})\n\n**Macro:** {}",
        code(&symbol.name),
        symbol.kind.as_str(),
        code(&symbol.macro_name)
    );
    if let Some(type_name) = &symbol.type_name {
        let label = if symbol.kind == SymbolKind::Function {
            "Recorded return type"
        } else {
            "Recorded type"
        };
        markdown.push_str(&format!("\n\n**{label}:** {}", code(type_name)));
    }
    if !symbol.bases.is_empty() {
        markdown.push_str(&format!(
            "\n\n**Recorded bases:**\n\n{}",
            code_block(&symbol.bases.join("\n"))
        ));
    }
    if !symbol.specifiers.is_empty() {
        let specifiers = symbol
            .specifiers
            .iter()
            .map(|specifier| match &specifier.value {
                Some(value) => format!("{} = {}", specifier.key, value),
                None => specifier.key.clone(),
            })
            .collect::<Vec<_>>()
            .join("\n");
        markdown.push_str(&format!(
            "\n\n**Recorded specifiers:**\n\n{}",
            code_block(&specifiers)
        ));
    }
    markdown
}

/// Both code spans and fenced blocks treat HTML and Markdown as literal text.
/// Padding prevents a leading/trailing backtick from merging with a delimiter;
/// multiline data needs a block because code spans normalize line breaks.
fn code(value: &str) -> String {
    if value.contains(['\r', '\n']) {
        return format!("\n\n{}", code_block(value));
    }
    let fence = backtick_fence(value, 1);
    format!("{fence} {value} {fence}")
}

fn code_block(value: &str) -> String {
    let fence = backtick_fence(value, 3);
    format!("{fence}text\n{value}\n{fence}")
}

/// A source value cannot terminate a delimiter longer than any run it contains.
fn backtick_fence(value: &str, minimum: usize) -> String {
    let mut longest = 0;
    let mut run = 0;
    for byte in value.bytes() {
        if byte == b'`' {
            run += 1;
            longest = longest.max(run);
        } else {
            run = 0;
        }
    }
    "`".repeat(minimum.max(longest + 1))
}

#[cfg(test)]
mod tests {
    use super::*;
    use ue_parser::Specifier;

    const HEADER: &str = "UCLASS(Blueprintable)\n\
        class AMyActor : public AActor, public IInteractable\n\
        {\n\
            GENERATED_BODY()\n\
            UPROPERTY(EditAnywhere, Category = \"Stats\")\n\
            float Health;\n\
        };\n";

    fn marked(source: &str) -> (Document, Position) {
        let offset = source.find('|').expect("test input needs a | caret");
        let document = Document::parse(source.replacen('|', "", 1));
        let position = document.lines.position(&document.text, offset);
        (document, position)
    }

    fn markdown(result: &Hover) -> &str {
        match &result.contents {
            HoverContents::Markup(markup) => {
                assert_eq!(markup.kind, MarkupKind::Markdown);
                &markup.value
            }
            _ => panic!("hover should use readable Markdown"),
        }
    }

    fn candidate(name: &str) -> UnrealSymbol {
        UnrealSymbol {
            name: name.to_string(),
            kind: SymbolKind::Property,
            macro_name: "UPROPERTY".to_string(),
            type_name: Some("float".to_string()),
            bases: Vec::new(),
            specifiers: Vec::new(),
            line: 1,
            byte_range: 0..0,
        }
    }

    fn assert_context(document: &Document, position: Position, macro_name: &str, in_meta: bool) {
        let context = macro_context_at(&document.text, document.offset(position).unwrap())
            .expect("fixture must exercise a recognized macro context");
        assert_eq!(context.macro_name, macro_name);
        assert_eq!(context.in_meta, in_meta);
    }

    #[test]
    fn a_valid_class_shows_only_its_recorded_facts() {
        let (document, position) = marked(&HEADER.replace("class AMyActor", "class AM|yActor"));
        let result = hover(&document, position, &[]).unwrap();
        let text = markdown(&result);
        assert!(text.contains("` AMyActor ` (class)"));
        assert!(text.contains("**Macro:** ` UCLASS `"));
        assert!(text.contains("**Recorded bases:**\n\n```text\nAActor\nIInteractable\n```"));
        assert!(text.contains("**Recorded specifiers:**\n\n```text\nBlueprintable\n```"));
        assert!(!text.contains("Recorded type"));
        assert_eq!(
            result.range,
            Some(Range::new(Position::new(1, 6), Position::new(1, 14)))
        );
    }

    #[test]
    fn a_valid_property_shows_its_type_and_macro_metadata() {
        let (document, position) = marked(&HEADER.replace("float Health", "float Hea|lth"));
        let result = hover(&document, position, &[]).unwrap();
        let text = markdown(&result);
        assert!(text.contains("` Health ` (property)"));
        assert!(text.contains("**Macro:** ` UPROPERTY `"));
        assert!(text.contains("**Recorded type:** ` float `"));
        assert!(text.contains("```text\nEditAnywhere\nCategory = Stats\n```"));
        assert!(
            !text.contains("AMyActor"),
            "a property has no recorded owner"
        );
        assert!(!text.contains("Recorded bases"));
    }

    #[test]
    fn a_prefix_or_end_caret_covers_the_full_identifier_in_utf16() {
        let document = Document::parse(format!("{HEADER}/* \u{1F427} */ AMyActor* Actor;"));
        for column in [9, 10, 12, 16, 17] {
            let result = hover(&document, Position::new(7, column), &[]).unwrap();
            assert!(markdown(&result).contains("` AMyActor ` (class)"));
            assert_eq!(
                result.range,
                Some(Range::new(Position::new(7, 9), Position::new(7, 17)))
            );
        }
    }

    #[test]
    fn local_symbols_take_precedence_over_candidate_metadata() {
        let (document, position) = marked(&HEADER.replace("float Health", "float Health|"));
        let mut stale = candidate("Health");
        stale.type_name = Some("int32".to_string());
        stale.specifiers.push(Specifier::pair("Category", "Stale"));
        let result = hover(&document, position, &[stale]).unwrap();
        let text = markdown(&result);
        assert!(text.contains("` float `"));
        assert!(text.contains("Category = Stats"));
        assert!(!text.contains("int32"));
        assert!(!text.contains("Stale"));
    }

    #[test]
    fn an_external_symbol_uses_the_first_matching_candidate_and_the_use_site_range() {
        let (document, position) = marked("float Other;\nExternal|;");
        let mut first = candidate("External");
        first.type_name = Some("FVector".to_string());
        first.line = 200;
        first.byte_range = 9000..9020;
        let result = hover(
            &document,
            position,
            &[candidate("Other"), first, candidate("External")],
        )
        .unwrap();
        assert!(markdown(&result).contains("` FVector `"));
        assert_eq!(
            result.range,
            Some(Range::new(Position::new(1, 0), Position::new(1, 8)))
        );
    }

    #[test]
    fn unknown_names_partial_names_and_invalid_positions_have_no_hover() {
        for source in ["Unknown|", "Hea|", "health|", "  | ", "|"] {
            let (document, position) = marked(source);
            assert!(hover(&document, position, &[candidate("Health")]).is_none());
        }
        let document = Document::parse("Health".to_string());
        assert!(hover(&document, Position::new(99, 0), &[candidate("Health")]).is_none());
    }

    #[test]
    fn shared_specifiers_use_the_enclosing_macros_own_description() {
        for (macro_name, specifier) in [
            ("UPROPERTY", "BlueprintCallable"),
            ("UFUNCTION", "BlueprintCallable"),
            ("UPROPERTY", "Category"),
            ("UFUNCTION", "Category"),
            ("UCLASS", "Blueprintable"),
            ("USTRUCT", "BlueprintType"),
            ("UENUM", "Flags"),
        ] {
            let (document, position) = marked(&format!("{macro_name}({specifier}|)"));
            assert_context(&document, position, macro_name, false);
            let result = hover(&document, position, &[candidate(specifier)]).unwrap();
            let expected = catalog::lookup(macro_name, specifier).unwrap();
            assert!(markdown(&result).contains(expected.description));
            assert!(markdown(&result).contains(&format!("specifier for ` {macro_name} `")));
            let start = (macro_name.len() + 1) as u32;
            assert_eq!(
                result.range,
                Some(Range::new(
                    Position::new(0, start),
                    Position::new(0, start + specifier.len() as u32)
                ))
            );
        }
    }

    #[test]
    fn specifier_lookup_uses_the_whole_word_and_catalog_casing_rules() {
        let (document, position) = marked("UPROPERTY(edi|tanywhere)");
        let result = hover(&document, position, &[]).unwrap();
        assert!(markdown(&result).contains("` EditAnywhere ` specifier"));
        assert_eq!(
            result.range,
            Some(Range::new(Position::new(0, 10), Position::new(0, 22)))
        );
    }

    #[test]
    fn unknown_or_wrong_macro_specifiers_never_fall_back_to_symbols() {
        for name in ["UnknownSpecifier", "Blueprintable", "Edit"] {
            let source = format!("UCLASS()\nclass {name} {{}};\nUPROPERTY({name}|)");
            let (document, position) = marked(&source);
            assert!(
                document.parsed.find(name).is_some(),
                "fixture needs a local collision"
            );
            assert_context(&document, position, "UPROPERTY", false);
            assert!(hover(&document, position, &[candidate(name)]).is_none());
        }
    }

    #[test]
    fn metadata_keys_are_not_top_level_specifiers_or_symbols() {
        for name in ["EditAnywhere", "Category", "meta", "ClampMin", "Health"] {
            let source = format!("{HEADER}UPROPERTY(meta = ({name}| = \"value\"))");
            let (document, position) = marked(&source);
            assert_context(&document, position, "UPROPERTY", true);
            assert!(hover(&document, position, &[candidate(name)]).is_none());
        }
    }

    #[test]
    fn the_meta_introducer_and_specifiers_after_a_closed_meta_group_still_have_hover() {
        for (source, name) in [
            ("UPROPERTY(me|ta = (ClampMin = \"0\"))", "meta"),
            (
                "UPROPERTY(meta = (ClampMin = \"0\"), EditAnywhere|)",
                "EditAnywhere",
            ),
        ] {
            let (document, position) = marked(source);
            assert_context(&document, position, "UPROPERTY", false);
            let result = hover(&document, position, &[]).unwrap();
            assert!(
                markdown(&result).contains(catalog::lookup("UPROPERTY", name).unwrap().description)
            );
        }
    }

    #[test]
    fn functions_report_a_return_type_without_fabricating_a_signature_or_owner() {
        let (document, position) = marked("Compute|");
        let mut function = candidate("Compute");
        function.kind = SymbolKind::Function;
        function.macro_name = "UFUNCTION".to_string();
        let result = hover(&document, position, &[function]).unwrap();
        assert_eq!(
            markdown(&result),
            "` Compute ` (function)\n\n**Macro:** ` UFUNCTION `\n\n**Recorded return type:** ` float `"
        );
    }

    #[test]
    fn absent_optional_facts_are_omitted_for_every_symbol_kind() {
        let (document, position) = marked("Symbol|");
        for kind in SymbolKind::ALL {
            let mut symbol = candidate("Symbol");
            symbol.kind = kind;
            symbol.type_name = None;
            let result = hover(&document, position, &[symbol]).unwrap();
            assert_eq!(
                markdown(&result),
                format!("` Symbol ` ({})\n\n**Macro:** ` UPROPERTY `", kind.as_str())
            );
        }
    }

    #[test]
    fn source_markdown_html_and_multiline_values_cannot_escape_literal_code() {
        let inline = "`<img src=x onerror=alert(1)>[run](command:run)&amp;`";
        let multiline = "line one\r\n```\n# forged heading\n````\r\n<script>alert(1)</script>\n![image](https://example.invalid/x)\n~~~\nlast line";
        let (document, position) = marked("Health|");
        let mut symbol = candidate("Health");
        symbol.macro_name = inline.to_string();
        symbol.type_name = Some(multiline.to_string());
        symbol.bases = vec![inline.to_string(), multiline.to_string()];
        symbol.specifiers = vec![Specifier::pair(inline, multiline)];
        let result = hover(&document, position, &[symbol]).unwrap();
        // Exact delimiters matter: a raw HTML tag/link is inert inside code,
        // and even a line consisting of four backticks cannot close this block.
        assert_eq!(
            markdown(&result),
            format!(
                "` Health ` (property)\n\n**Macro:** `` {inline} ``\n\n\
                 **Recorded type:** \n\n`````text\n{multiline}\n`````\n\n\
                 **Recorded bases:**\n\n`````text\n{inline}\n{multiline}\n`````\n\n\
                 **Recorded specifiers:**\n\n`````text\n{inline} = {multiline}\n`````"
            )
        );
    }

    #[test]
    fn literal_delimiters_handle_backticks_and_either_line_ending() {
        for value in ["`", "a``b", "```", " leading and trailing ", "<b>x</b>"] {
            let fence = backtick_fence(value, 1);
            assert_eq!(code(value), format!("{fence} {value} {fence}"));
        }
        assert_eq!(backtick_fence("` `` ```", 1), "````");
        for value in ["one\ntwo", "one\rtwo", "one\r\ntwo"] {
            assert_eq!(code(value), format!("\n\n```text\n{value}\n```"));
        }
    }
}
