//! What the caret is sitting inside.
//!
//! Completion has to know whether the user is typing a specifier inside
//! `UPROPERTY(...)` or ordinary C++. A parse cannot answer that: the macro the
//! user is mid-way through typing has no closing paren yet, so it is not a
//! macro invocation as far as the scanner is concerned. Instead this walks
//! backwards from the caret tracking paren depth, which works on text that is
//! still syntactically broken — the normal state of a buffer being edited.

use crate::catalog;

/// A caret inside a reflection macro's specifier list.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MacroContext {
    /// The enclosing macro, spelled as the catalog spells it.
    pub macro_name: &'static str,
    /// Byte offset of the macro's opening parenthesis.
    pub open_paren: usize,
    /// Whether the caret is nested inside that macro's `meta = (...)` group,
    /// where the ordinary specifier list does not apply.
    pub in_meta: bool,
}

/// How far back to look before giving up.
///
/// A specifier list spans a line or two; anything further means the caret is
/// not in one, and scanning to the top of a 10k-line header on every keystroke
/// would cost more than the answer is worth.
const MAX_LOOKBACK: usize = 4096;

/// Whether the quote at `index` is escaped by a preceding backslash run.
fn is_escaped(bytes: &[u8], index: usize) -> bool {
    let mut backslashes = 0;
    let mut i = index;
    while i > 0 && bytes[i - 1] == b'\\' {
        backslashes += 1;
        i -= 1;
    }
    backslashes % 2 == 1
}

/// The identifier introducing the parenthesis at `paren`, and where it starts.
///
/// Handles both `UPROPERTY(` and the `meta = (` spelling, including the
/// whitespace UE codebases put around the equals sign.
fn introducer(text: &str, paren: usize) -> (&str, usize) {
    let bytes = text.as_bytes();
    let mut i = paren;

    let skip_space = |bytes: &[u8], mut i: usize| {
        while i > 0 && bytes[i - 1].is_ascii_whitespace() {
            i -= 1;
        }
        i
    };

    i = skip_space(bytes, i);
    if i > 0 && bytes[i - 1] == b'=' {
        i = skip_space(bytes, i - 1);
    }

    let end = i;
    while i > 0 && (bytes[i - 1].is_ascii_alphanumeric() || bytes[i - 1] == b'_') {
        i -= 1;
    }
    (&text[i..end], i)
}

/// The reflection macro enclosing `offset`, if any.
pub fn macro_context_at(text: &str, offset: usize) -> Option<MacroContext> {
    let bytes = text.as_bytes();
    let mut i = offset.min(bytes.len());
    let floor = i.saturating_sub(MAX_LOOKBACK);
    let mut depth = 0usize;
    let mut in_meta = false;

    while i > floor {
        i -= 1;
        match bytes[i] {
            // Step over a string literal wholesale; parens and semicolons
            // inside `Category = "A(B;"` are text, not structure.
            b'"' => {
                while i > floor {
                    i -= 1;
                    if bytes[i] == b'"' && !is_escaped(bytes, i) {
                        break;
                    }
                }
            }
            b')' => depth += 1,
            b'(' => {
                if depth > 0 {
                    depth -= 1;
                    continue;
                }
                let (ident, start) = introducer(text, i);
                if let Some(macro_name) = catalog::MACRO_NAMES.iter().copied().find(|m| *m == ident)
                {
                    return Some(MacroContext {
                        macro_name,
                        open_paren: i,
                        in_meta,
                    });
                }
                if ident.eq_ignore_ascii_case("meta") {
                    // Keep going outward: the macro is one level further out.
                    in_meta = true;
                    i = start;
                    continue;
                }
                // An unmatched paren belonging to something else — a function
                // call or a declaration — means this is ordinary C++.
                return None;
            }
            // At depth zero these end a statement or a block, so the caret
            // cannot be inside an argument list that started before them.
            b';' | b'{' | b'}' if depth == 0 => return None,
            _ => {}
        }
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Resolves context at the caret marked `|`, which is stripped first.
    fn at(marked: &str) -> Option<MacroContext> {
        let offset = marked.find('|').expect("test input needs a | caret");
        let text = marked.replace('|', "");
        macro_context_at(&text, offset)
    }

    #[test]
    fn finds_the_macro_around_a_half_typed_specifier() {
        let ctx = at("UPROPERTY(Edit|").unwrap();
        assert_eq!(ctx.macro_name, "UPROPERTY");
        assert_eq!(ctx.open_paren, 9);
        assert!(!ctx.in_meta);
    }

    #[test]
    fn survives_an_earlier_complete_specifier_list() {
        let ctx = at("UFUNCTION(BlueprintCallable, Category = \"Combat\", Ser|").unwrap();
        assert_eq!(ctx.macro_name, "UFUNCTION");
    }

    #[test]
    fn parentheses_inside_a_string_do_not_close_the_list() {
        // The legacy regexes used `([^)]*)` and broke on exactly this shape.
        let ctx = at("UPROPERTY(meta = (ClampMin = \"0.0)\"), Edit|").unwrap();
        assert_eq!(ctx.macro_name, "UPROPERTY");
        assert!(!ctx.in_meta, "caret is back out at the top level");
    }

    #[test]
    fn nested_meta_group_is_reported_as_such() {
        let ctx = at("UPROPERTY(EditAnywhere, meta = (ClampM|").unwrap();
        assert_eq!(ctx.macro_name, "UPROPERTY");
        assert!(ctx.in_meta);
    }

    #[test]
    fn ordinary_cpp_is_not_a_macro_context() {
        assert!(at("void Tick(float Delta|").is_none());
        assert!(at("    Health = FMath::Clamp(Val|").is_none());
        assert!(at("float Heal|").is_none());
    }

    #[test]
    fn a_closed_macro_on_an_earlier_line_does_not_leak() {
        let source = "UPROPERTY(EditAnywhere)\nfloat Heal|";
        assert!(at(source).is_none());
    }

    #[test]
    fn multi_line_specifier_lists_are_still_found() {
        let source = "UCLASS(\n    Blueprintable,\n    Abstract,\n    Min|";
        let ctx = at(source).unwrap();
        assert_eq!(ctx.macro_name, "UCLASS");
    }
}
