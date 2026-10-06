//! Data model for symbols extracted from Unreal Engine headers.

use std::ops::Range;

/// Kind of Unreal reflection symbol, determined by the macro that declares it.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SymbolKind {
    Class,
    Struct,
    Interface,
    Enum,
    Function,
    Property,
    Delegate,
}

impl SymbolKind {
    /// The reflection macro kind that introduces this symbol, if it is declared
    /// by exactly one macro name.
    pub fn as_str(self) -> &'static str {
        match self {
            SymbolKind::Class => "class",
            SymbolKind::Struct => "struct",
            SymbolKind::Interface => "interface",
            SymbolKind::Enum => "enum",
            SymbolKind::Function => "function",
            SymbolKind::Property => "property",
            SymbolKind::Delegate => "delegate",
        }
    }

    /// Inverse of [`SymbolKind::as_str`], for reading a kind back out of storage.
    pub fn from_name(name: &str) -> Option<SymbolKind> {
        Some(match name {
            "class" => SymbolKind::Class,
            "struct" => SymbolKind::Struct,
            "interface" => SymbolKind::Interface,
            "enum" => SymbolKind::Enum,
            "function" => SymbolKind::Function,
            "property" => SymbolKind::Property,
            "delegate" => SymbolKind::Delegate,
            _ => return None,
        })
    }

    /// Every kind, so callers can enumerate without matching exhaustively.
    pub const ALL: [SymbolKind; 7] = [
        SymbolKind::Class,
        SymbolKind::Struct,
        SymbolKind::Interface,
        SymbolKind::Enum,
        SymbolKind::Function,
        SymbolKind::Property,
        SymbolKind::Delegate,
    ];
}

/// One entry in a reflection macro's argument list.
///
/// `EditAnywhere` is a bare flag, `Category="Stats"` is a key/value pair, and
/// `meta=(ClampMin="0.0", ClampMax="1.0")` keeps its nested list intact as the
/// raw value — recoverable via [`Specifier::nested`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Specifier {
    pub key: String,
    pub value: Option<String>,
}

impl Specifier {
    /// A bare flag with no `=value`, e.g. `EditAnywhere`.
    pub fn flag(key: impl Into<String>) -> Self {
        Specifier {
            key: key.into(),
            value: None,
        }
    }

    /// A `key=value` pair. Surrounding quotes are already stripped from `value`.
    pub fn pair(key: impl Into<String>, value: impl Into<String>) -> Self {
        Specifier {
            key: key.into(),
            value: Some(value.into()),
        }
    }

    /// Parses a parenthesised value as its own specifier list, so the contents of
    /// `meta=(AllowPrivateAccess="true")` are reachable as structured data.
    ///
    /// Returns an empty vector when the value is absent or not parenthesised.
    pub fn nested(&self) -> Vec<Specifier> {
        let Some(value) = self.value.as_deref() else {
            return Vec::new();
        };
        let trimmed = value.trim();
        match trimmed.strip_prefix('(').and_then(|v| v.strip_suffix(')')) {
            Some(inner) => crate::specifiers::parse_specifiers(inner),
            None => Vec::new(),
        }
    }

    /// Case-insensitive key comparison. Unreal specifier keys are conventionally
    /// PascalCase but the compiler accepts any casing.
    pub fn is(&self, key: &str) -> bool {
        self.key.eq_ignore_ascii_case(key)
    }
}

/// A single reflected symbol and the macro metadata attached to it.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UnrealSymbol {
    /// Identifier as written in the source, e.g. `AMyActor` or `Health`.
    pub name: String,
    pub kind: SymbolKind,
    /// The declaring macro, e.g. `UCLASS` or `DECLARE_DYNAMIC_DELEGATE_OneParam`.
    pub macro_name: String,
    pub specifiers: Vec<Specifier>,
    /// Declared type for properties and return type for functions.
    pub type_name: Option<String>,
    /// Base classes/interfaces for classes and structs, in declaration order.
    pub bases: Vec<String>,
    /// 1-based line of the declaring macro.
    pub line: usize,
    /// Byte range of the declaring macro within the original source.
    pub byte_range: Range<usize>,
}

impl UnrealSymbol {
    /// Looks up a specifier by key, ignoring case.
    pub fn specifier(&self, key: &str) -> Option<&Specifier> {
        self.specifiers.iter().find(|s| s.is(key))
    }

    /// Whether the macro carries the given specifier, ignoring case.
    pub fn has_specifier(&self, key: &str) -> bool {
        self.specifier(key).is_some()
    }

    /// Value of a specifier by key, ignoring case. `None` for bare flags.
    pub fn specifier_value(&self, key: &str) -> Option<&str> {
        self.specifier(key).and_then(|s| s.value.as_deref())
    }
}

/// All reflected symbols found in one translation unit, in source order.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ParsedFile {
    pub symbols: Vec<UnrealSymbol>,
}

impl ParsedFile {
    /// Symbols of a single kind, in source order.
    pub fn of_kind(&self, kind: SymbolKind) -> impl Iterator<Item = &UnrealSymbol> {
        self.symbols.iter().filter(move |s| s.kind == kind)
    }

    /// First symbol with this exact name.
    pub fn find(&self, name: &str) -> Option<&UnrealSymbol> {
        self.symbols.iter().find(|s| s.name == name)
    }
}

/// Rich information established by declaration syntax, never inferred from a
/// file name, a previously encountered symbol, or a matching short name.
///
/// All ranges are half-open UTF-8 **byte** offsets into the original source.
/// [`UnrealSymbol::byte_range`] and `line` still identify the reflection macro.
/// `None` means unknown/unsupported, not an empty or guessed value.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct SymbolMetadata {
    /// Lexically enclosing named class/struct/namespace, fully qualified as
    /// spelled. For an explicitly qualified declarator, its written scope.
    /// Not a resolved type identity; anonymous/local/recovered scopes are absent.
    pub owner: Option<String>,
    /// Syntax-qualified name (not unique across overloads or translation units).
    pub qualified_name: Option<String>,
    /// Original declaration header, excluding a body and trailing semicolon.
    /// Includes written parameters, qualifiers, defaults, and return type; no
    /// overload resolution, type canonicalization, or signature reconstruction.
    pub signature: Option<String>,
    /// Exact original source slice at `declaration_range`, without its leading
    /// reflection macro. A class/function definition includes its body.
    /// A delegate's declaration is its self-contained macro invocation.
    pub declaration: Option<String>,
    /// Directly attached standalone comments, verbatim (including comment
    /// delimiters). Blank lines or intervening code stop attachment. Clients
    /// must render this as untrusted source text, not raw markup.
    pub documentation: Option<String>,
    /// Range of the final unqualified identifier token.
    pub name_range: Option<Range<usize>>,
    /// Range of the syntax node used for `declaration`.
    pub declaration_range: Option<Range<usize>>,
}

/// Opt-in rich parse result. Parallel vectors have identical lengths and are
/// paired by **position**, never by name, preserving duplicates and overloads.
/// Existing [`ParsedFile`] and [`UnrealSymbol`] struct literals remain unchanged.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ParsedFileWithMetadata {
    pub symbols: Vec<UnrealSymbol>,
    pub metadata: Vec<SymbolMetadata>,
}
