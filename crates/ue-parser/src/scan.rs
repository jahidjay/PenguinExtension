//! Lexical pre-pass over an Unreal header.
//!
//! `tree-sitter-cpp` cannot parse raw UE headers: the `*_API` export macros,
//! `GENERATED_BODY()`, the reflection macros themselves and `UMETA(...)` on
//! enumerators are unexpanded preprocessor tokens, and each one cascades into
//! `ERROR`/`MISSING` nodes — a class's `base_class_clause` is lost entirely and
//! its body collapses into an `initializer_list` of errors.
//!
//! This pass locates those macros and overwrites each span with **spaces of
//! equal length**, keeping newlines so byte offsets, line numbers and columns
//! still map back to the original file. The grammar then sees plain C++.
//!
//! Argument capture tracks paren nesting, so `meta=(...)` is captured whole
//! rather than truncated at the first `)`.

use std::ops::Range;

use crate::model::SymbolKind;

/// A reflection macro invocation found in the source.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MacroInvocation {
    /// Macro identifier, e.g. `UPROPERTY`.
    pub name: String,
    /// Raw text between the outer parentheses, empty when there are none.
    pub args: String,
    /// Byte range of the whole invocation, including name and parentheses.
    pub range: Range<usize>,
    /// What the macro declares.
    pub kind: MacroKind,
}

/// What a recorded macro introduces.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum MacroKind {
    /// Attaches to the next C++ declaration in the source.
    Declaration(SymbolKind),
    /// Self-contained: name is the first argument (second for RetVal variants).
    Delegate,
}

/// Outcome of the lexical pass.
#[derive(Debug, Clone)]
pub struct ScanResult {
    /// Recorded macros in source order.
    pub macros: Vec<MacroInvocation>,
    /// Source with every UE macro blanked, byte-for-byte the same length.
    pub sanitized: String,
}

/// How a macro identifier should be treated.
enum Class {
    /// Record it, then blank it.
    Record(MacroKind),
    /// Blank it without recording; it only exists to break the grammar.
    Blank,
    /// Ordinary C++ identifier, leave alone.
    Keep,
}

/// Macros that declare a reflected symbol.
fn declaration_kind(name: &str) -> Option<SymbolKind> {
    Some(match name {
        "UCLASS" => SymbolKind::Class,
        "USTRUCT" => SymbolKind::Struct,
        "UINTERFACE" => SymbolKind::Interface,
        "UENUM" => SymbolKind::Enum,
        "UFUNCTION" => SymbolKind::Function,
        "UPROPERTY" => SymbolKind::Property,
        _ => return None,
    })
}

/// Macros that carry no symbol but must be removed before parsing.
///
/// `UDELEGATE` is here rather than recorded: it only decorates the
/// `DECLARE_*DELEGATE*` line that follows, which is what yields the symbol.
const NOISE: &[&str] = &[
    "GENERATED_BODY",
    "GENERATED_UCLASS_BODY",
    "GENERATED_USTRUCT_BODY",
    "GENERATED_UINTERFACE_BODY",
    "GENERATED_IINTERFACE_BODY",
    "UMETA",
    "UPARAM",
    "UDELEGATE",
];

fn classify(name: &str) -> Class {
    if let Some(kind) = declaration_kind(name) {
        return Class::Record(MacroKind::Declaration(kind));
    }
    // `DECLARE_EVENT*` is deliberately excluded: its first argument is the
    // owning class, not the delegate, so recording it would store wrong names.
    if name.starts_with("DECLARE_") && name.contains("DELEGATE") && !name.contains("EVENT") {
        return Class::Record(MacroKind::Delegate);
    }
    if NOISE.contains(&name) {
        return Class::Blank;
    }
    // Module export macros: `MYGAME_API`, `ENGINE_API`. Uppercase only, so a
    // normal identifier that happens to end in `_api` is untouched.
    if name.ends_with("_API")
        && name
            .bytes()
            .all(|b| b.is_ascii_uppercase() || b.is_ascii_digit() || b == b'_')
    {
        return Class::Blank;
    }
    Class::Keep
}

/// Scans `source`, recording reflection macros and producing a sanitized copy.
pub fn scan(source: &str) -> ScanResult {
    let bytes = source.as_bytes();
    let mut out = bytes.to_vec();
    let mut macros = Vec::new();
    let mut i = 0usize;

    while i < bytes.len() {
        match bytes[i] {
            b'/' if bytes.get(i + 1) == Some(&b'/') => {
                i = skip_line_comment(bytes, i);
            }
            b'/' if bytes.get(i + 1) == Some(&b'*') => {
                i = skip_block_comment(bytes, i);
            }
            b'"' | b'\'' => {
                i = skip_literal(bytes, i);
            }
            b if is_ident_start(b) => {
                let end = ident_end(bytes, i);
                let name = &source[i..end];
                match classify(name) {
                    Class::Keep => {}
                    Class::Blank => {
                        let span_end = match arg_span(bytes, end) {
                            Some((_, close)) => close + 1,
                            None => end,
                        };
                        blank(&mut out, i..span_end);
                        i = span_end;
                        continue;
                    }
                    Class::Record(kind) => {
                        let (args, span_end) = match arg_span(bytes, end) {
                            Some((open, close)) => (source[open + 1..close].to_string(), close + 1),
                            None => (String::new(), end),
                        };
                        blank(&mut out, i..span_end);
                        macros.push(MacroInvocation {
                            name: name.to_string(),
                            args,
                            range: i..span_end,
                            kind,
                        });
                        i = span_end;
                        continue;
                    }
                }
                i = end;
            }
            _ => i += 1,
        }
    }

    ScanResult {
        macros,
        sanitized: String::from_utf8(out).expect("blanking preserves UTF-8 validity"),
    }
}

/// Overwrites `range` with spaces, preserving newlines so line/column maps hold.
fn blank(out: &mut [u8], range: Range<usize>) {
    for b in &mut out[range] {
        if *b != b'\n' && *b != b'\r' {
            *b = b' ';
        }
    }
}

/// Finds a macro's parenthesised argument list starting at or after `from`,
/// skipping intervening whitespace and comments.
///
/// Returns the byte index of the opening and matching closing paren.
fn arg_span(bytes: &[u8], from: usize) -> Option<(usize, usize)> {
    let open = skip_trivia(bytes, from);
    if bytes.get(open) != Some(&b'(') {
        return None;
    }
    let close = match_paren(bytes, open)?;
    Some((open, close))
}

/// Index of the `)` matching the `(` at `open`, honouring nesting, literals and
/// comments. This is what keeps `meta=(...)` intact.
fn match_paren(bytes: &[u8], open: usize) -> Option<usize> {
    let mut depth = 0i32;
    let mut i = open;
    while i < bytes.len() {
        match bytes[i] {
            b'/' if bytes.get(i + 1) == Some(&b'/') => {
                i = skip_line_comment(bytes, i);
                continue;
            }
            b'/' if bytes.get(i + 1) == Some(&b'*') => {
                i = skip_block_comment(bytes, i);
                continue;
            }
            b'"' | b'\'' => {
                i = skip_literal(bytes, i);
                continue;
            }
            b'(' => depth += 1,
            b')' => {
                depth -= 1;
                if depth == 0 {
                    return Some(i);
                }
            }
            _ => {}
        }
        i += 1;
    }
    None
}

pub(crate) fn skip_trivia(bytes: &[u8], mut i: usize) -> usize {
    loop {
        match bytes.get(i) {
            Some(b) if b.is_ascii_whitespace() => i += 1,
            Some(b'/') if bytes.get(i + 1) == Some(&b'/') => i = skip_line_comment(bytes, i),
            Some(b'/') if bytes.get(i + 1) == Some(&b'*') => i = skip_block_comment(bytes, i),
            _ => return i,
        }
    }
}

fn skip_line_comment(bytes: &[u8], from: usize) -> usize {
    let mut i = from + 2;
    while i < bytes.len() && bytes[i] != b'\n' {
        i += 1;
    }
    i
}

fn skip_block_comment(bytes: &[u8], from: usize) -> usize {
    let mut i = from + 2;
    while i < bytes.len() {
        if bytes[i] == b'*' && bytes.get(i + 1) == Some(&b'/') {
            return i + 2;
        }
        i += 1;
    }
    bytes.len()
}

/// Advances past a string or char literal, honouring backslash escapes.
fn skip_literal(bytes: &[u8], from: usize) -> usize {
    let quote = bytes[from];
    let mut i = from + 1;
    while i < bytes.len() {
        match bytes[i] {
            b'\\' => i += 2,
            b if b == quote => return i + 1,
            _ => i += 1,
        }
    }
    bytes.len()
}

fn is_ident_start(b: u8) -> bool {
    b.is_ascii_alphabetic() || b == b'_'
}

fn ident_end(bytes: &[u8], from: usize) -> usize {
    let mut i = from;
    while i < bytes.len() && (bytes[i].is_ascii_alphanumeric() || bytes[i] == b'_') {
        i += 1;
    }
    i
}

/// Byte offsets at which each line starts, for O(log n) line lookup.
pub(crate) fn line_starts(source: &str) -> Vec<usize> {
    let mut starts = vec![0usize];
    starts.extend(
        source
            .bytes()
            .enumerate()
            .filter(|&(_, b)| b == b'\n')
            .map(|(i, _)| i + 1),
    );
    starts
}

/// 1-based line number containing `byte`.
pub(crate) fn line_of(starts: &[usize], byte: usize) -> usize {
    match starts.binary_search(&byte) {
        Ok(i) => i + 1,
        Err(i) => i,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sanitizing_preserves_length_and_lines() {
        let src = "UCLASS()\nclass MYGAME_API AFoo : public AActor\n{\n GENERATED_BODY()\n};\n";
        let result = scan(src);
        assert_eq!(result.sanitized.len(), src.len());
        assert_eq!(
            result.sanitized.lines().count(),
            src.lines().count(),
            "newlines must survive blanking"
        );
        assert!(!result.sanitized.contains("MYGAME_API"));
        assert!(!result.sanitized.contains("GENERATED_BODY"));
        assert!(result.sanitized.contains("class  "));
        assert!(result.sanitized.contains("public AActor"));
    }

    #[test]
    fn captures_nested_parens_in_args() {
        let src =
            r#"UPROPERTY(EditAnywhere, meta=(ClampMin="0.0", ClampMax="100.0")) float Health;"#;
        let result = scan(src);
        assert_eq!(result.macros.len(), 1);
        assert_eq!(
            result.macros[0].args,
            r#"EditAnywhere, meta=(ClampMin="0.0", ClampMax="100.0")"#
        );
        assert!(result.sanitized.trim_start().starts_with("float Health;"));
    }

    #[test]
    fn multiline_macro_args_are_captured() {
        let src = "UFUNCTION(\n    BlueprintCallable,\n    Category=\"Combat\"\n)\nvoid Fire();";
        let result = scan(src);
        assert_eq!(result.macros.len(), 1);
        assert!(result.macros[0].args.contains("BlueprintCallable"));
        assert!(result.macros[0].args.contains("Category=\"Combat\""));
        assert_eq!(result.sanitized.len(), src.len());
    }

    #[test]
    fn macros_in_comments_and_strings_are_ignored() {
        let src = "// UCLASS()\n/* UPROPERTY() */\nconst char* s = \"UFUNCTION()\";";
        let result = scan(src);
        assert!(result.macros.is_empty());
        assert_eq!(result.sanitized, src);
    }

    #[test]
    fn paren_inside_string_does_not_terminate_args() {
        let src = r#"UPROPERTY(meta=(DisplayName="Close ) Paren"))int32 N;"#;
        let result = scan(src);
        assert_eq!(result.macros.len(), 1);
        assert_eq!(
            result.macros[0].args,
            r#"meta=(DisplayName="Close ) Paren")"#
        );
    }

    #[test]
    fn lowercase_api_identifier_is_kept() {
        let src = "void use_api();";
        assert_eq!(scan(src).sanitized, src);
    }

    #[test]
    fn delegate_macro_is_recorded_event_is_not() {
        let src = "DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnHit, float, Amount);\n\
                   DECLARE_EVENT(FThing, FEvt);";
        let result = scan(src);
        assert_eq!(result.macros.len(), 1);
        assert_eq!(result.macros[0].kind, MacroKind::Delegate);
        assert!(result.sanitized.contains("DECLARE_EVENT"));
    }

    #[test]
    fn line_lookup_is_one_based() {
        let starts = line_starts("a\nbb\nccc");
        assert_eq!(line_of(&starts, 0), 1);
        assert_eq!(line_of(&starts, 2), 2);
        assert_eq!(line_of(&starts, 5), 3);
    }
}
