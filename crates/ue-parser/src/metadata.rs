//! Conservative syntax-only metadata. Recovery nodes are never identity evidence.
use crate::{scan, specifiers, MacroInvocation, MacroKind, SymbolKind, SymbolMetadata};
use tree_sitter::Node;

pub(crate) fn comments(root: Node<'_>) -> Vec<Node<'_>> {
    let mut out = Vec::new();
    let mut cursor = root.walk();
    loop {
        if cursor.node().kind() == "comment" {
            out.push(cursor.node());
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

#[allow(clippy::too_many_arguments)]
pub(crate) fn extract(
    source: &str,
    sanitized: &str,
    mac: &MacroInvocation,
    name: &str,
    declaration: Option<Node<'_>>,
    root: Option<Node<'_>>,
    comments: &[Node<'_>],
    next_macro: Option<usize>,
) -> SymbolMetadata {
    let mut out = SymbolMetadata::default();
    if let Some(context) =
        root.and_then(|r| r.descendant_for_byte_range(mac.range.start, mac.range.end))
    {
        if invalid_macro_context(context) {
            return out;
        }
    }
    if !source[mac.range.clone()].ends_with(')') {
        return out;
    }
    if mac.kind == MacroKind::Delegate {
        if !known_delegate(&mac.name) {
            return out;
        }
        let parts = specifiers::split_top_level(&mac.args, ',');
        let index = usize::from(mac.name.contains("_RetVal"));
        let Some(arg) = parts.get(index) else {
            return out;
        };
        if !identifier(arg.trim()) || arg.trim() != name {
            return out;
        }
        let args_start = mac.range.end - 1 - mac.args.len();
        let prefix: usize = parts[..index].iter().map(|p| p.len() + 1).sum();
        let start = args_start + prefix + arg.len() - arg.trim_start().len();
        out.name_range = Some(start..start + name.len());
        out.declaration_range = Some(mac.range.clone());
        out.declaration = Some(source[mac.range.clone()].to_string());
        out.signature = out.declaration.clone();
        out.documentation = attached_documentation(source, comments, mac.range.start);
        if let Some(root) = root {
            if let Some(context) = root.descendant_for_byte_range(mac.range.start, mac.range.end) {
                if let Some(owner) = lexical_owner(Some(context), sanitized) {
                    set_names(&mut out, owner, name);
                }
            }
        }
        return out;
    }
    let Some(node) = declaration else { return out };
    // Legacy recovery can attach a stranded macro to an unrelated later node.
    // Do not promote that into metadata, or skip another reflection macro.
    if next_macro.is_some_and(|next| next < node.start_byte())
        || scan::skip_trivia(sanitized.as_bytes(), mac.range.end) != node.start_byte()
        || node.has_error()
        || has_recovery_ancestor(node)
    {
        return out;
    }
    let Some(identity) = identity_node(node) else {
        return out;
    };
    if !matches_kind(identity, mac.kind) {
        return out;
    }
    let Some(declarator) = identity
        .child_by_field_name("name")
        .or_else(|| identity.child_by_field_name("declarator"))
    else {
        return out;
    };
    let Some((token, explicit)) = name_token(declarator, source) else {
        return out;
    };
    if &source[token.byte_range()] != name {
        return out;
    }
    out.name_range = Some(token.byte_range());
    out.declaration_range = Some(node.byte_range());
    out.declaration = Some(source[node.byte_range()].to_string());
    let header_end = identity
        .child_by_field_name("body")
        .map(|body| body.start_byte())
        .unwrap_or(node.end_byte());
    out.signature = Some(
        source[node.start_byte()..header_end]
            .trim()
            .trim_end_matches(';')
            .trim_end()
            .to_string(),
    );
    out.documentation = attached_documentation(source, comments, node.start_byte())
        .or_else(|| attached_documentation(source, comments, mac.range.start));
    if let Some(owner) = explicit {
        // Retain written qualification; do not resolve relative scope names.
        set_names(&mut out, Some(owner), name);
    } else if let Some(owner) = lexical_owner(identity.parent(), sanitized) {
        set_names(&mut out, owner, name);
    }
    out
}

fn set_names(out: &mut SymbolMetadata, owner: Option<String>, name: &str) {
    out.qualified_name = Some(match owner.as_deref() {
        Some("::") => format!("::{name}"),
        Some(owner) => format!("{owner}::{name}"),
        None => name.to_string(),
    });
    out.owner = owner;
}

fn has_recovery_ancestor(mut node: Node<'_>) -> bool {
    while let Some(parent) = node.parent() {
        if parent.is_error()
            || parent.is_missing()
            || matches!(parent.kind(), "preproc_def" | "preproc_function_def")
        {
            return true;
        }
        node = parent;
    }
    false
}

fn identity_node(node: Node<'_>) -> Option<Node<'_>> {
    if node.kind() == "template_declaration" {
        return node
            .named_children(&mut node.walk())
            .find(|n| crate::is_declaration_kind(n.kind()))
            .and_then(identity_node);
    }
    if let Some(ty) = node.child_by_field_name("type") {
        if matches!(
            ty.kind(),
            "class_specifier" | "struct_specifier" | "enum_specifier" | "union_specifier"
        ) {
            return Some(ty);
        }
    }
    Some(node)
}

fn matches_kind(node: Node<'_>, kind: MacroKind) -> bool {
    match kind {
        MacroKind::Declaration(SymbolKind::Class | SymbolKind::Interface) => {
            node.kind() == "class_specifier"
        }
        MacroKind::Declaration(SymbolKind::Struct) => node.kind() == "struct_specifier",
        MacroKind::Declaration(SymbolKind::Enum) => node.kind() == "enum_specifier",
        MacroKind::Declaration(SymbolKind::Function) => node
            .child_by_field_name("declarator")
            .is_some_and(has_function),
        MacroKind::Declaration(SymbolKind::Property) => node
            .child_by_field_name("declarator")
            .is_some_and(|n| !has_function(n)),
        _ => false,
    }
}

fn has_function(node: Node<'_>) -> bool {
    node.kind() == "function_declarator"
        || node
            .child_by_field_name("declarator")
            .is_some_and(has_function)
}

fn name_token<'t>(node: Node<'t>, source: &str) -> Option<(Node<'t>, Option<String>)> {
    match node.kind() {
        "identifier" | "field_identifier" | "type_identifier" => Some((node, None)),
        "qualified_identifier" => {
            let name = node.child_by_field_name("name")?;
            let (token, _) = name_token(name, source)?;
            let prefix = source[node.start_byte()..token.start_byte()].trim();
            let owner = if prefix == "::" {
                "::"
            } else {
                prefix.strip_suffix("::")?.trim()
            };
            Some((token, Some(owner.to_string())))
        }
        "pointer_declarator"
        | "reference_declarator"
        | "abstract_pointer_declarator"
        | "function_declarator"
        | "array_declarator"
        | "init_declarator"
        | "parenthesized_declarator" => name_token(
            node.child_by_field_name("declarator")
                .or_else(|| node.named_child(0))?,
            source,
        ),
        _ => None,
    }
}

// Outer Option: scope is known. Inner Option: named owner vs global scope.
fn lexical_owner(mut node: Option<Node<'_>>, source: &str) -> Option<Option<String>> {
    let mut names = Vec::new();
    while let Some(current) = node {
        match current.kind() {
            "namespace_definition" | "class_specifier" | "struct_specifier" | "union_specifier" => {
                let name = current.child_by_field_name("name")?;
                if name.has_error() || name.is_missing() {
                    return None;
                }
                names.push(source[name.byte_range()].trim().to_string());
            }
            "ERROR"
            | "function_definition"
            | "lambda_expression"
            | "preproc_def"
            | "preproc_function_def" => return None,
            _ => {}
        }
        node = current.parent();
    }
    names.reverse();
    Some((!names.is_empty()).then(|| names.join("::")))
}

fn identifier(text: &str) -> bool {
    let mut bytes = text.bytes();
    bytes
        .next()
        .is_some_and(|b| b.is_ascii_alphabetic() || b == b'_')
        && bytes.all(|b| b.is_ascii_alphanumeric() || b == b'_')
}

fn attached_documentation(source: &str, comments: &[Node<'_>], anchor: usize) -> Option<String> {
    let mut end = anchor;
    let mut attached = Vec::new();
    let index = comments.partition_point(|node| node.end_byte() <= anchor);
    for comment in comments[..index].iter().rev() {
        let gap = &source[comment.end_byte()..end];
        if !gap.trim().is_empty() || gap.bytes().filter(|b| *b == b'\n').count() > 1 {
            break;
        }
        let line_start = source[..comment.start_byte()]
            .rfind('\n')
            .map(|i| i + 1)
            .unwrap_or(0);
        if !source[line_start..comment.start_byte()].trim().is_empty() {
            break; // Do not borrow a previous declaration's trailing comment.
        }
        attached.push(&source[comment.byte_range()]);
        end = comment.start_byte();
    }
    attached.reverse();
    (!attached.is_empty()).then(|| attached.join("\n"))
}

// Only these UE macro families have the name argument convention used above.
// The scanner intentionally remains permissive for legacy symbol discovery.
fn known_delegate(name: &str) -> bool {
    let name = [
        "_OneParam",
        "_TwoParams",
        "_ThreeParams",
        "_FourParams",
        "_FiveParams",
        "_SixParams",
        "_SevenParams",
        "_EightParams",
        "_NineParams",
    ]
    .iter()
    .find_map(|suffix| name.strip_suffix(suffix))
    .unwrap_or(name);
    matches!(
        name,
        "DECLARE_DELEGATE"
            | "DECLARE_MULTICAST_DELEGATE"
            | "DECLARE_DYNAMIC_DELEGATE"
            | "DECLARE_DYNAMIC_MULTICAST_DELEGATE"
            | "DECLARE_DELEGATE_RetVal"
            | "DECLARE_DYNAMIC_DELEGATE_RetVal"
            | "DECLARE_TS_MULTICAST_DELEGATE"
    )
}

fn invalid_macro_context(mut node: Node<'_>) -> bool {
    loop {
        if node.is_error()
            || node.is_missing()
            || matches!(
                node.kind(),
                "string_literal"
                    | "raw_string_literal"
                    | "char_literal"
                    | "comment"
                    | "preproc_def"
                    | "preproc_function_def"
                    | "preproc_arg"
            )
        {
            return true;
        }
        match node.parent() {
            Some(parent) => node = parent,
            None => return false,
        }
    }
}
