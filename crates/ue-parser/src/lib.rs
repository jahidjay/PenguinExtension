//! Unreal Engine header parser built on `tree-sitter-cpp`.
//!
//! Parsing happens in three stages:
//!
//! 1. [`scan()`] records every reflection macro and blanks all UE preprocessor
//!    macros with equal-length spaces, so the grammar sees valid C++ while byte
//!    offsets still match the original file.
//! 2. `tree-sitter-cpp` parses the sanitized text into a real AST.
//! 3. Each recorded macro is attached to the first declaration that starts after
//!    it, and the name, type and base classes are read off that node.
//!
//! ```no_run
//! let mut parser = ue_parser::UnrealParser::new().unwrap();
//! let parsed = parser.parse("UCLASS()\nclass AFoo : public AActor { };");
//! assert_eq!(parsed.symbols[0].name, "AFoo");
//! ```

mod metadata;
pub mod model;
pub mod scan;
pub mod specifiers;

use std::fmt;

use tree_sitter::{Node, Parser};

pub use model::{
    ParsedFile, ParsedFileWithMetadata, Specifier, SymbolKind, SymbolMetadata, UnrealSymbol,
};

/// Version of syntax/metadata extraction persisted by `ue-db`. Bump when
/// extraction semantics change so unchanged cached headers are re-indexed.
pub const METADATA_VERSION: u32 = 1;
pub use scan::{scan, MacroInvocation, MacroKind, ScanResult};
pub use specifiers::parse_specifiers;

/// Failure to construct the parser.
#[derive(Debug)]
pub enum ParseError {
    /// The C++ grammar was rejected, normally an ABI mismatch between
    /// `tree-sitter` and `tree-sitter-cpp`.
    Language(tree_sitter::LanguageError),
}

impl fmt::Display for ParseError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            ParseError::Language(e) => write!(f, "failed to load the C++ grammar: {e}"),
        }
    }
}

impl std::error::Error for ParseError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            ParseError::Language(e) => Some(e),
        }
    }
}

/// Extracts Unreal reflection symbols from C++ headers.
///
/// Holds a `tree-sitter` parser, so reuse one instance across files rather than
/// constructing per file.
pub struct UnrealParser {
    parser: Parser,
}

impl UnrealParser {
    pub fn new() -> Result<Self, ParseError> {
        let mut parser = Parser::new();
        parser
            .set_language(&tree_sitter_cpp::language())
            .map_err(ParseError::Language)?;
        Ok(UnrealParser { parser })
    }

    /// Parses one header's contents. Byte ranges and line numbers in the result
    /// refer to `source` as given.
    ///
    /// Malformed input degrades rather than failing: macros whose declaration
    /// cannot be identified are dropped, and the symbols that did resolve are
    /// still returned.
    pub fn parse(&mut self, source: &str) -> ParsedFile {
        let parsed = self.parse_internal(source, false);
        ParsedFile {
            symbols: parsed.symbols,
        }
    }

    /// Parses symbols plus optional, syntax-proven rich metadata in one pass.
    /// The result pairs metadata by symbol position, not short/qualified name.
    /// Unsupported or recovery syntax yields absent metadata; legacy macro
    /// locations remain unchanged. See [`SymbolMetadata`] for range/doc rules.
    pub fn parse_with_metadata(&mut self, source: &str) -> ParsedFileWithMetadata {
        self.parse_internal(source, true)
    }

    fn parse_internal(&mut self, source: &str, rich: bool) -> ParsedFileWithMetadata {
        let ScanResult { macros, sanitized } = scan::scan(source);
        if macros.is_empty() {
            return ParsedFileWithMetadata::default();
        }

        let tree = self.parser.parse(&sanitized, None);
        let line_starts = scan::line_starts(source);
        let mut symbols = Vec::with_capacity(macros.len());
        let mut metadata = Vec::with_capacity(if rich { macros.len() } else { 0 });
        let candidates = tree
            .as_ref()
            .map(|t| declaration_nodes(t.root_node()))
            .unwrap_or_default();
        let comments = if rich {
            tree.as_ref()
                .map(|t| metadata::comments(t.root_node()))
                .unwrap_or_default()
        } else {
            Vec::new()
        };

        for (index, mac) in macros.iter().enumerate() {
            let line = scan::line_of(&line_starts, mac.range.start);
            let specifiers = specifiers::parse_specifiers(&mac.args);

            let declaration = match mac.kind {
                MacroKind::Declaration(_) => {
                    resolve_declaration(&candidates, &sanitized, mac.range.end)
                }
                MacroKind::Delegate => None,
            };
            let resolved = match mac.kind {
                MacroKind::Delegate => delegate_name(&mac.name, &mac.args).map(|name| Resolved {
                    name,
                    type_name: None,
                    bases: Vec::new(),
                }),
                MacroKind::Declaration(_) => {
                    declaration.as_ref().map(|(_, resolved)| resolved.clone())
                }
            };

            let Some(resolved) = resolved else { continue };

            if rich {
                metadata.push(metadata::extract(
                    source,
                    &sanitized,
                    mac,
                    &resolved.name,
                    declaration.as_ref().map(|(node, _)| *node),
                    tree.as_ref().map(|t| t.root_node()),
                    &comments,
                    macros.get(index + 1).map(|next| next.range.start),
                ));
            }
            symbols.push(UnrealSymbol {
                name: resolved.name,
                kind: match mac.kind {
                    MacroKind::Declaration(kind) => kind,
                    MacroKind::Delegate => SymbolKind::Delegate,
                },
                macro_name: mac.name.clone(),
                specifiers,
                type_name: resolved.type_name,
                bases: resolved.bases,
                line,
                byte_range: mac.range.clone(),
            });
        }

        ParsedFileWithMetadata { symbols, metadata }
    }
}

/// What a declaration node contributed to its symbol.
#[derive(Clone)]
struct Resolved {
    name: String,
    type_name: Option<String>,
    bases: Vec<String>,
}

/// Delegate name argument; return-value variants put the return type first.
fn delegate_name(macro_name: &str, args: &str) -> Option<String> {
    let args = specifiers::split_top_level(args, ',');
    let index = usize::from(macro_name.contains("_RetVal"));
    let name = args.get(index)?.trim();
    (!name.is_empty()).then(|| name.to_string())
}

/// Finds the declaration a macro annotates and reads its identity from the AST.
fn resolve_declaration<'t>(
    candidates: &[Node<'t>],
    src: &str,
    macro_end: usize,
) -> Option<(Node<'t>, Resolved)> {
    let index = candidates.partition_point(|n| n.start_byte() < macro_end);
    let node = *candidates.get(index)?;
    describe(node, src).map(|resolved| (node, resolved))
}

/// Node kinds that can carry a reflection macro.
fn is_declaration_kind(kind: &str) -> bool {
    matches!(
        kind,
        "class_specifier"
            | "struct_specifier"
            | "union_specifier"
            | "enum_specifier"
            | "declaration"
            | "field_declaration"
            | "function_definition"
            | "template_declaration"
    )
}

/// Collects declaration nodes in pre-order (document order).
fn declaration_nodes<'t>(root: Node<'t>) -> Vec<Node<'t>> {
    let mut out = Vec::new();
    let mut cursor = root.walk();
    loop {
        let node = cursor.node();
        if is_declaration_kind(node.kind()) {
            out.push(node);
        }
        if cursor.goto_first_child() {
            continue;
        }
        loop {
            if cursor.goto_next_sibling() {
                break;
            }
            if !cursor.goto_parent() {
                return out;
            }
        }
    }
}

/// Reads the name, declared type and base classes off a declaration node.
fn describe(node: Node<'_>, src: &str) -> Option<Resolved> {
    match node.kind() {
        "class_specifier" | "struct_specifier" | "union_specifier" => Some(Resolved {
            name: text(node.child_by_field_name("name")?, src)?.to_string(),
            type_name: None,
            bases: base_classes(node, src),
        }),
        "enum_specifier" => Some(Resolved {
            name: text(node.child_by_field_name("name")?, src)?.to_string(),
            type_name: node
                .child_by_field_name("base")
                .and_then(|b| text(b, src))
                .map(str::to_string),
            bases: Vec::new(),
        }),
        "template_declaration" => node
            .named_children(&mut node.walk())
            .find(|c| is_declaration_kind(c.kind()))
            .and_then(|inner| describe(inner, src)),
        "declaration" | "field_declaration" | "function_definition" => {
            let type_node = node.child_by_field_name("type");
            // `UCLASS`/`USTRUCT` sometimes land on a wrapping declaration whose
            // type field is the specifier itself; unwrap to that.
            if let Some(ty) = type_node {
                if is_declaration_kind(ty.kind()) && ty.kind().ends_with("_specifier") {
                    return describe(ty, src);
                }
            }
            let declarator = node.child_by_field_name("declarator")?;
            let (name, decoration) = declarator_name(declarator, src)?;
            let type_name = type_node
                .and_then(|ty| text(ty, src))
                .map(|ty| format!("{ty}{decoration}"));
            Some(Resolved {
                name,
                type_name,
                bases: Vec::new(),
            })
        }
        _ => None,
    }
}

/// Base classes in declaration order, dropping `public`/`private` keywords.
fn base_classes(node: Node<'_>, src: &str) -> Vec<String> {
    let Some(clause) = node
        .children(&mut node.walk())
        .find(|c| c.kind() == "base_class_clause")
    else {
        return Vec::new();
    };
    clause
        .named_children(&mut clause.walk())
        .filter(|c| c.kind() != "access_specifier")
        .filter_map(|c| text(c, src))
        .map(str::to_string)
        .collect()
}

/// Unwraps a declarator to the declared identifier, accumulating `*` and `&`.
///
/// Returns the name and the pointer/reference decoration to append to the type,
/// so `AActor* Target;` yields `("Target", "*")`.
fn declarator_name(node: Node<'_>, src: &str) -> Option<(String, String)> {
    match node.kind() {
        "identifier" | "field_identifier" | "type_identifier" => {
            Some((text(node, src)?.to_string(), String::new()))
        }
        "qualified_identifier" => {
            // `Outer::Inner` — the declared name is the final segment.
            let name = node.child_by_field_name("name")?;
            declarator_name(name, src)
        }
        "pointer_declarator" | "reference_declarator" | "abstract_pointer_declarator" => {
            let inner = node
                .child_by_field_name("declarator")
                .or_else(|| node.named_child(0))?;
            let mark = if node.kind() == "reference_declarator" {
                "&"
            } else {
                "*"
            };
            let (name, decoration) = declarator_name(inner, src)?;
            Some((name, format!("{mark}{decoration}")))
        }
        "function_declarator"
        | "array_declarator"
        | "init_declarator"
        | "parenthesized_declarator" => {
            let inner = node
                .child_by_field_name("declarator")
                .or_else(|| node.named_child(0))?;
            declarator_name(inner, src)
        }
        _ => None,
    }
}

fn text<'s>(node: Node<'_>, src: &'s str) -> Option<&'s str> {
    src.get(node.start_byte()..node.end_byte())
        .map(str::trim)
        .filter(|t| !t.is_empty())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn parse(src: &str) -> ParsedFile {
        UnrealParser::new().expect("grammar loads").parse(src)
    }

    #[test]
    fn source_without_macros_yields_nothing() {
        let parsed = parse("class Plain { int x; };");
        assert!(parsed.symbols.is_empty());
    }

    #[test]
    fn delegate_name_is_first_argument() {
        assert_eq!(
            delegate_name(
                "DECLARE_DYNAMIC_DELEGATE_OneParam",
                "FOnHealthChanged, float, NewHealth"
            )
            .as_deref(),
            Some("FOnHealthChanged")
        );
        assert_eq!(delegate_name("DECLARE_DELEGATE", "").as_deref(), None);
    }
}
