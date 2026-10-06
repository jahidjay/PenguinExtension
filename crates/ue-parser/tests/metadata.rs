//! Metadata is opt-in and never uses names as row identity.
use ue_parser::{SymbolMetadata, UnrealParser};
fn parse(source: &str) -> ue_parser::ParsedFileWithMetadata {
    UnrealParser::new().unwrap().parse_with_metadata(source)
}

#[test]
fn overloads_and_same_names_keep_correct_lexical_owners() {
    let source = r#"
namespace Game {
/** Actor docs. */
UCLASS() class AFirst {
    GENERATED_BODY()
    /// Sends one shot.
    UFUNCTION() void Fire(int32 Count = 1) const;
    /** Sends a stronger shot. */
    UFUNCTION() void Fire(float Strength);
    UPROPERTY() int32 Health;
};
UCLASS() class ASecond { UPROPERTY() float Health; };
UFUNCTION() void Fire();
}
"#;
    let parsed = parse(source);
    let legacy = UnrealParser::new().unwrap().parse(source);
    assert_eq!(parsed.symbols, legacy.symbols);
    assert_eq!(parsed.symbols.len(), parsed.metadata.len());
    let fires: Vec<_> = parsed
        .symbols
        .iter()
        .zip(&parsed.metadata)
        .filter(|(s, _)| s.name == "Fire")
        .collect();
    assert_eq!(fires.len(), 3);
    assert_eq!(fires[0].1.owner.as_deref(), Some("Game::AFirst"));
    assert_eq!(
        fires[1].1.qualified_name.as_deref(),
        Some("Game::AFirst::Fire")
    );
    assert_eq!(fires[2].1.owner.as_deref(), Some("Game"));
    assert_eq!(
        fires[0].1.signature.as_deref(),
        Some("void Fire(int32 Count = 1) const")
    );
    assert_eq!(
        fires[1].1.signature.as_deref(),
        Some("void Fire(float Strength)")
    );
    assert_eq!(
        fires[0].1.documentation.as_deref(),
        Some("/// Sends one shot.")
    );
    assert_eq!(
        parsed.metadata[0].documentation.as_deref(),
        Some("/** Actor docs. */")
    );
    let health: Vec<_> = parsed
        .symbols
        .iter()
        .zip(&parsed.metadata)
        .filter(|(s, _)| s.name == "Health")
        .collect();
    assert_eq!(health[0].1.owner.as_deref(), Some("Game::AFirst"));
    assert_eq!(health[1].1.owner.as_deref(), Some("Game::ASecond"));
    for (symbol, metadata) in parsed.symbols.iter().zip(&parsed.metadata) {
        assert_eq!(
            &source[metadata.name_range.clone().expect("name range")],
            symbol.name
        );
        assert_eq!(
            &source[metadata.declaration_range.clone().unwrap()],
            metadata.declaration.as_deref().unwrap()
        );
        assert!(source[symbol.byte_range.clone()].starts_with(&symbol.macro_name));
    }
}

#[test]
fn nested_namespaces_types_and_explicit_qualification_are_syntax_only() {
    let source = r#"
namespace One { namespace Two {
struct Outer {
    USTRUCT() struct Inner { UPROPERTY() int Value; };
};
}}
UFUNCTION() void Written::Scope::Call(int Arg);
UFUNCTION() void Global();
namespace { UPROPERTY() int Hidden; }
"#;
    let parsed = parse(source);
    let metadata = |name: &str| -> &SymbolMetadata {
        &parsed.metadata[parsed.symbols.iter().position(|s| s.name == name).unwrap()]
    };
    assert_eq!(
        metadata("Inner").qualified_name.as_deref(),
        Some("One::Two::Outer::Inner")
    );
    assert_eq!(
        metadata("Value").owner.as_deref(),
        Some("One::Two::Outer::Inner")
    );
    assert_eq!(metadata("Call").owner.as_deref(), Some("Written::Scope"));
    assert_eq!(
        metadata("Call").signature.as_deref(),
        Some("void Written::Scope::Call(int Arg)")
    );
    assert_eq!(metadata("Global").owner, None);
    assert_eq!(metadata("Global").qualified_name.as_deref(), Some("Global"));
    assert_eq!(metadata("Hidden").owner, None);
    assert_eq!(metadata("Hidden").qualified_name, None);
}

#[test]
fn documentation_requires_adjacent_standalone_comments() {
    let source = r#"
// Detached comment.

UPROPERTY() int NoDocs;
int Other; // Not the next property docs.
UPROPERTY() int NoTrailingDocs;
// First line.
// Second line.
UPROPERTY() int Documented;
UPROPERTY()
/** Between macro and declaration. */
int Between;
"#;
    let parsed = parse(source);
    assert_eq!(parsed.metadata[0].documentation, None);
    assert_eq!(parsed.metadata[1].documentation, None);
    assert_eq!(
        parsed.metadata[2].documentation.as_deref(),
        Some(
            "// First line.
// Second line."
        )
    );
    assert_eq!(
        parsed.metadata[3].documentation.as_deref(),
        Some("/** Between macro and declaration. */")
    );
}

#[test]
fn unknown_or_misattached_declarations_have_no_rich_metadata() {
    for source in [
        "struct End { UPROPERTY() }; int Unrelated;",
        "UPROPERTY() UPROPERTY() int Value;",
        "UFUNCTION() int NotAFunction;",
        "UPROPERTY() int Broken[;",
    ] {
        let parsed = parse(source);
        if let Some(metadata) = parsed.metadata.first() {
            assert_eq!(*metadata, SymbolMetadata::default(), "{source}");
        }
    }
    let parsed = parse("void LocalScope() { UPROPERTY() int Local; }");
    let metadata = &parsed.metadata[0];
    assert!(metadata.owner.is_none() && metadata.qualified_name.is_none());
}

#[test]
fn ranges_are_utf8_byte_offsets_and_preserve_original_declarations() {
    let source = "// cafe λ
UCLASS()
class MODULE_API AThing {
 UFUNCTION() int Run(int V) const { return V; }
};"
    .replace(
        char::from(10),
        &format!("{}{}", char::from(13), char::from(10)),
    );
    let parsed = parse(&source);
    assert_eq!(parsed.symbols.len(), 2);
    assert_eq!(parsed.symbols[0].line, 2);
    assert_eq!(
        parsed.metadata[0].signature.as_deref(),
        Some("class MODULE_API AThing")
    );
    let function = &parsed.metadata[1];
    assert_eq!(function.signature.as_deref(), Some("int Run(int V) const"));
    assert_eq!(
        function.declaration.as_deref(),
        Some("int Run(int V) const { return V; }")
    );
    assert_eq!(&source[function.name_range.clone().unwrap()], "Run");
}

#[test]
fn delegates_use_macro_argument_ranges_including_return_value_variants() {
    let source = r#"
namespace Events {
/// Notified on value change.
DECLARE_DYNAMIC_DELEGATE_OneParam(FOnValue, int32, Value);
DECLARE_DELEGATE_RetVal_OneParam(bool, FCanRun, int32);
}
"#;
    let parsed = parse(source);
    assert_eq!(parsed.symbols.len(), 2);
    assert_eq!(parsed.symbols[1].name, "FCanRun");
    for (symbol, metadata) in parsed.symbols.iter().zip(&parsed.metadata) {
        assert_eq!(&source[metadata.name_range.clone().unwrap()], symbol.name);
        assert_eq!(metadata.owner.as_deref(), Some("Events"));
        assert_eq!(
            metadata.declaration_range.as_ref(),
            Some(&symbol.byte_range)
        );
    }
    assert_eq!(
        parsed.metadata[0].documentation.as_deref(),
        Some("/// Notified on value change.")
    );
}

#[test]
fn unknown_delegate_conventions_and_macro_definitions_are_not_guessed() {
    let parsed = parse("DECLARE_CUSTOM_DELEGATE_MYSTERY(SomeName, Something);");
    assert_eq!(
        parsed.symbols.len(),
        1,
        "legacy permissive discovery stays compatible"
    );
    assert_eq!(parsed.metadata[0], SymbolMetadata::default());
    let parsed = parse(
        "#define EXAMPLE DECLARE_DELEGATE(FPretend);
",
    );
    for metadata in parsed.metadata {
        assert_eq!(metadata, SymbolMetadata::default());
    }
}

#[test]
fn delegate_inside_class_uses_syntax_owner() {
    let parsed = parse("class Outer { DECLARE_DELEGATE(FOnChanged); }; DECLARE_DELEGATE(FGlobal);");
    assert_eq!(parsed.metadata[0].owner.as_deref(), Some("Outer"));
    assert_eq!(parsed.metadata[1].owner, None);
    assert_eq!(
        parsed.metadata[1].qualified_name.as_deref(),
        Some("FGlobal")
    );
}
