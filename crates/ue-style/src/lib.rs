//! Conservative, source-local Unreal reflection style checks.
//!
//! This pure library performs no I/O, indexing, macro expansion, or LSP work.
//! Diagnostics describe style evidence, not compiler errors, and provide no
//! fixes. Unknown specifiers are not interpreted. Incomplete or ambiguous
//! macro/declaration shapes are skipped rather than guessed.
//!
//! ```
//! let source = "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool bReady;";
//! let parsed = ue_parser::UnrealParser::new().unwrap().parse(source);
//! let diagnostics = ue_style::check(source, &parsed, &ue_style::StyleConfig::default());
//! assert_eq!(diagnostics[0].code, ue_style::codes::BLUEPRINT_ACCESS_CONFLICT);
//! ```

mod evidence;

use serde::{Deserialize, Serialize};
use std::ops::Range;
use ue_parser::{ParsedFile, SymbolKind};

/// Stable diagnostic identifiers. Messages may evolve; match these codes instead.
pub mod codes {
    pub const BLUEPRINT_ACCESS_CONFLICT: &str = "UE_STYLE_001";
    pub const EDIT_VISIBILITY_CONFLICT: &str = "UE_STYLE_002";
    pub const STRUCT_PREFIX: &str = "UE_STYLE_101";
    pub const ENUM_PREFIX: &str = "UE_STYLE_102";
    pub const BOOL_PROPERTY_PREFIX: &str = "UE_STYLE_103";
}

/// Rule switches, also accepted as a partial serde object.
/// Only contradictory specifier checks are enabled by default.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(default)]
pub struct StyleConfig {
    /// Bare BlueprintReadOnly and BlueprintReadWrite on one UPROPERTY.
    pub blueprint_access_conflicts: bool,
    /// More than one distinct Edit*/Visible* mode on one UPROPERTY.
    pub edit_visibility_conflicts: bool,
    /// Reflected structs must begin with F and have a nonempty suffix.
    pub struct_prefix: bool,
    /// Reflected enums must begin with E and have a nonempty suffix.
    pub enum_prefix: bool,
    /// Scalar bool properties use b followed by UpperCamelCase.
    /// Aliases, pointers, references, arrays and integer bitfields are not inferred.
    pub bool_property_prefix: bool,
}

impl Default for StyleConfig {
    fn default() -> Self {
        Self {
            blueprint_access_conflicts: true,
            edit_visibility_conflicts: true,
            struct_prefix: false,
            enum_prefix: false,
            bool_property_prefix: false,
        }
    }
}

/// Style severities, deliberately not compiler-error classifications.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Severity {
    Warning,
    Hint,
}

/// A source-local style observation with a stable code.
#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
pub struct Diagnostic {
    pub code: &'static str,
    pub severity: Severity,
    pub message: String,
    /// Nonempty, half-open UTF-8 byte offsets into the supplied source.
    /// Contradictions highlight the complete macro; naming highlights the name.
    pub byte_range: Range<usize>,
}

const EDIT_MODES: &[&str] = &[
    "EditAnywhere",
    "EditDefaultsOnly",
    "EditInstanceOnly",
    "VisibleAnywhere",
    "VisibleDefaultsOnly",
    "VisibleInstanceOnly",
];

/// Check existing parser symbols against their source without modifying either.
///
/// Supply the same source snapshot used for `parsed`. Invalid ranges, unsupported
/// macro names/kinds, and lexically incomplete macros are ignored. Explicit
/// top-level bare flags are read from verified source tokens, not substrings or
/// permissively parsed metadata. Comments, strings and nested metadata cannot
/// create a conflict. Unknown bare flags are ignored; unrecognised argument
/// syntax is skipped conservatively. Preprocessor directives are skipped; branch
/// conditions are not evaluated.
///
/// Naming additionally requires a directly adjacent, complete declaration of
/// the expected shape. Results are sorted by range then code/message and exact
/// duplicates are removed, independent of the order of `parsed.symbols`.
pub fn check(source: &str, parsed: &ParsedFile, config: &StyleConfig) -> Vec<Diagnostic> {
    if parsed.symbols.is_empty()
        || !(config.blueprint_access_conflicts
            || config.edit_visibility_conflicts
            || config.struct_prefix
            || config.enum_prefix
            || config.bool_property_prefix)
    {
        return Vec::new();
    }
    let evidence = evidence::Source::new(source);
    let mut diagnostics = Vec::new();
    for symbol in &parsed.symbols {
        if !matches!(
            (symbol.kind, symbol.macro_name.as_str()),
            (SymbolKind::Property, "UPROPERTY")
                | (SymbolKind::Struct, "USTRUCT")
                | (SymbolKind::Enum, "UENUM")
        ) {
            continue;
        }
        let Some(invocation) = evidence.invocation(symbol) else {
            continue;
        };

        if symbol.kind == SymbolKind::Property {
            if config.blueprint_access_conflicts
                && invocation.has_flag("BlueprintReadOnly")
                && invocation.has_flag("BlueprintReadWrite")
            {
                diagnostics.push(Diagnostic {
                    code: codes::BLUEPRINT_ACCESS_CONFLICT,
                    severity: Severity::Warning,
                    message: "UPROPERTY combines BlueprintReadOnly and BlueprintReadWrite; these describe contradictory Blueprint access modes.".into(),
                    byte_range: symbol.byte_range.clone(),
                });
            }
            if config.edit_visibility_conflicts {
                let modes: Vec<_> = EDIT_MODES
                    .iter()
                    .copied()
                    .filter(|mode| invocation.has_flag(mode))
                    .collect();
                if modes.len() > 1 {
                    diagnostics.push(Diagnostic {
                        code: codes::EDIT_VISIBILITY_CONFLICT,
                        severity: Severity::Warning,
                        message: format!(
                            "UPROPERTY combines mutually exclusive edit/visibility modes: {}.",
                            modes.join(", ")
                        ),
                        byte_range: symbol.byte_range.clone(),
                    });
                }
            }
        }
        let naming = match symbol.kind {
            SymbolKind::Struct if config.struct_prefix && !prefixed(&symbol.name, 'F') => Some((
                codes::STRUCT_PREFIX,
                "Reflected struct names conventionally begin with F followed by a name.",
            )),
            SymbolKind::Enum if config.enum_prefix && !prefixed(&symbol.name, 'E') => Some((
                codes::ENUM_PREFIX,
                "Reflected enum names conventionally begin with E followed by a name.",
            )),
            SymbolKind::Property
                if config.bool_property_prefix
                    && symbol.type_name.as_deref() == Some("bool")
                    && !bool_name(&symbol.name) =>
            {
                Some((
                    codes::BOOL_PROPERTY_PREFIX,
                    "Reflected bool property names conventionally use bUpperCamelCase.",
                ))
            }
            _ => None,
        };
        if let Some((code, message)) = naming {
            if let Some(byte_range) = evidence.declaration_name(symbol, invocation.end) {
                diagnostics.push(Diagnostic {
                    code,
                    severity: Severity::Hint,
                    message: message.into(),
                    byte_range,
                });
            }
        }
    }
    diagnostics.sort_by(|a, b| {
        (a.byte_range.start, a.byte_range.end, a.code, &a.message).cmp(&(
            b.byte_range.start,
            b.byte_range.end,
            b.code,
            &b.message,
        ))
    });
    diagnostics.dedup();
    diagnostics
}

fn prefixed(name: &str, prefix: char) -> bool {
    name.starts_with(prefix) && name.chars().count() > 1
}

fn bool_name(name: &str) -> bool {
    let Some(tail) = name.strip_prefix('b') else {
        return false;
    };
    tail.chars().next().is_some_and(char::is_uppercase) && tail.chars().all(char::is_alphanumeric)
}
