//! Splitter for the argument list of a reflection macro.
//!
//! The legacy C# indexer captured macro arguments with `([^)]*)`, which stops
//! at the first `)` and truncates anything containing `meta=(...)`. This module
//! splits at depth-0 commas instead, tracking paren/bracket/brace nesting and
//! string/char literals, so nested specifier lists survive intact.

use crate::model::Specifier;

/// Parses the raw text between a reflection macro's outer parentheses into a
/// list of specifiers.
///
/// - Splits entries at commas that sit at nesting depth 0 and outside literals.
/// - Splits each entry at its first depth-0 `=` into key/value.
/// - Strips one layer of surrounding double quotes from values.
/// - Parenthesised values (`meta=(...)`) are kept verbatim; use
///   [`Specifier::nested`] to descend into them.
pub fn parse_specifiers(args: &str) -> Vec<Specifier> {
    split_top_level(args, ',')
        .into_iter()
        .filter_map(|entry| {
            let entry = entry.trim();
            if entry.is_empty() {
                return None;
            }
            match find_top_level(entry, '=') {
                Some(eq) => {
                    let key = entry[..eq].trim();
                    let value = unquote(entry[eq + 1..].trim());
                    if key.is_empty() {
                        None
                    } else {
                        Some(Specifier::pair(key, value))
                    }
                }
                None => Some(Specifier::flag(entry)),
            }
        })
        .collect()
}

/// Splits `text` at every occurrence of `sep` that is at nesting depth 0 and
/// outside string/char literals.
pub fn split_top_level(text: &str, sep: char) -> Vec<&str> {
    let mut parts = Vec::new();
    let mut start = 0;
    for pos in top_level_positions(text, sep) {
        parts.push(&text[start..pos]);
        start = pos + sep.len_utf8();
    }
    parts.push(&text[start..]);
    parts
}

/// Byte offset of the first depth-0, non-literal occurrence of `sep`.
fn find_top_level(text: &str, sep: char) -> Option<usize> {
    top_level_positions(text, sep).into_iter().next()
}

/// Byte offsets of all depth-0, non-literal occurrences of `sep`.
fn top_level_positions(text: &str, sep: char) -> Vec<usize> {
    let mut positions = Vec::new();
    let mut depth = 0i32;
    let mut chars = text.char_indices().peekable();
    while let Some((i, c)) = chars.next() {
        match c {
            '(' | '[' | '{' => depth += 1,
            ')' | ']' | '}' => depth -= 1,
            '"' | '\'' => {
                // Skip to the closing quote, honouring backslash escapes.
                let quote = c;
                while let Some((_, n)) = chars.next() {
                    match n {
                        '\\' => {
                            chars.next();
                        }
                        _ if n == quote => break,
                        _ => {}
                    }
                }
            }
            _ if c == sep && depth == 0 => positions.push(i),
            _ => {}
        }
    }
    positions
}

/// Removes one layer of surrounding double quotes, if present.
fn unquote(value: &str) -> &str {
    value
        .strip_prefix('"')
        .and_then(|v| v.strip_suffix('"'))
        .unwrap_or(value)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn bare_flags() {
        let specs = parse_specifiers("EditAnywhere, BlueprintReadWrite");
        assert_eq!(
            specs,
            vec![
                Specifier::flag("EditAnywhere"),
                Specifier::flag("BlueprintReadWrite"),
            ]
        );
    }

    #[test]
    fn key_value_unquoted() {
        let specs = parse_specifiers(r#"Category="Stats|Core""#);
        assert_eq!(specs, vec![Specifier::pair("Category", "Stats|Core")]);
    }

    #[test]
    fn nested_meta_survives_depth_0_split() {
        // The exact shape the legacy `([^)]*)` regex truncated (§2.4).
        let specs = parse_specifiers(
            r#"EditAnywhere, meta=(ClampMin="0.0", ClampMax="100.0"), Category="Stats""#,
        );
        assert_eq!(specs.len(), 3);
        assert_eq!(specs[0], Specifier::flag("EditAnywhere"));
        assert_eq!(specs[1].key, "meta");
        assert_eq!(
            specs[1].value.as_deref(),
            Some(r#"(ClampMin="0.0", ClampMax="100.0")"#)
        );
        assert_eq!(specs[2], Specifier::pair("Category", "Stats"));
    }

    #[test]
    fn nested_meta_expands_to_structured_pairs() {
        let specs = parse_specifiers(r#"meta=(ClampMin="0.0", ClampMax="100.0")"#);
        let nested = specs[0].nested();
        assert_eq!(
            nested,
            vec![
                Specifier::pair("ClampMin", "0.0"),
                Specifier::pair("ClampMax", "100.0"),
            ]
        );
    }

    #[test]
    fn commas_inside_quotes_do_not_split() {
        let specs = parse_specifiers(r#"meta=(DisplayName="Health, Max"), Transient"#);
        assert_eq!(specs.len(), 2);
        assert_eq!(specs[1], Specifier::flag("Transient"));
    }

    #[test]
    fn equals_inside_nested_value_is_not_a_split_point() {
        let specs = parse_specifiers(r#"meta=(EditCondition="bEnabled==true")"#);
        assert_eq!(specs[0].key, "meta");
        let nested = specs[0].nested();
        assert_eq!(
            nested,
            vec![Specifier::pair("EditCondition", "bEnabled==true")]
        );
    }

    #[test]
    fn empty_input_yields_no_specifiers() {
        assert!(parse_specifiers("").is_empty());
        assert!(parse_specifiers("  ,  ").is_empty());
    }
}
