use ue_parser::{ParsedFile, SymbolKind, UnrealParser};
use ue_style::{check, codes, Diagnostic, Severity, StyleConfig};

fn parse(source: &str) -> ParsedFile {
    UnrealParser::new().expect("grammar loads").parse(source)
}

fn lint(source: &str) -> Vec<Diagnostic> {
    let diagnostics = check(source, &parse(source), &StyleConfig::default());
    valid_ranges(source, &diagnostics);
    diagnostics
}

fn naming() -> StyleConfig {
    StyleConfig {
        struct_prefix: true,
        enum_prefix: true,
        bool_property_prefix: true,
        ..StyleConfig::default()
    }
}

fn valid_ranges(source: &str, diagnostics: &[Diagnostic]) {
    for d in diagnostics {
        assert!(d.byte_range.start < d.byte_range.end, "{d:?}");
        assert!(source.get(d.byte_range.clone()).is_some(), "{d:?}");
        assert!(!d.message.is_empty());
    }
}

#[test]
fn defaults_and_partial_serde_configs() {
    let config = StyleConfig::default();
    assert!(config.blueprint_access_conflicts && config.edit_visibility_conflicts);
    assert!(!config.struct_prefix && !config.enum_prefix && !config.bool_property_prefix);
    assert_eq!(serde_json::from_str::<StyleConfig>("{}").unwrap(), config);
    let custom: StyleConfig =
        serde_json::from_str(r#"{"struct_prefix":true,"blueprint_access_conflicts":false}"#)
            .unwrap();
    assert!(custom.struct_prefix && custom.edit_visibility_conflicts);
    assert!(!custom.blueprint_access_conflicts && !custom.bool_property_prefix);
    assert_eq!(
        serde_json::from_str::<StyleConfig>(&serde_json::to_string(&custom).unwrap()).unwrap(),
        custom
    );
    assert!(serde_json::from_str::<StyleConfig>(r#"{"struct_prefix":"yes"}"#).is_err());
}

#[test]
fn exact_codes_severities_and_macro_ranges() {
    let src = "// header\nUPROPERTY(BlueprintReadOnly, BlueprintReadWrite, EditAnywhere, VisibleAnywhere) bool bReady;";
    let ds = lint(src);
    assert_eq!(ds.len(), 2);
    assert_eq!(ds[0].code, codes::BLUEPRINT_ACCESS_CONFLICT);
    assert_eq!(ds[1].code, codes::EDIT_VISIBILITY_CONFLICT);
    for d in &ds {
        assert_eq!(d.severity, Severity::Warning);
        assert_eq!(
            &src[d.byte_range.clone()],
            "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite, EditAnywhere, VisibleAnywhere)"
        );
    }
    let json = serde_json::to_value(&ds[0]).unwrap();
    assert_eq!(json["code"], "UE_STYLE_001");
    assert_eq!(json["severity"], "warning");
    assert_eq!(json["byte_range"]["start"], 10);
}

#[test]
fn all_distinct_edit_modes_conflict_but_duplicates_do_not() {
    let modes = [
        "EditAnywhere",
        "EditDefaultsOnly",
        "EditInstanceOnly",
        "VisibleAnywhere",
        "VisibleDefaultsOnly",
        "VisibleInstanceOnly",
    ];
    for (a, left) in modes.iter().enumerate() {
        for (b, right) in modes.iter().enumerate() {
            let src = format!("UPROPERTY({left}, {right}) float Value;");
            let ds = lint(&src);
            assert_eq!(ds.len(), usize::from(a != b), "{src}");
            if let Some(d) = ds.first() {
                assert_eq!(d.code, codes::EDIT_VISIBILITY_CONFLICT);
            }
        }
    }
    assert!(lint("UPROPERTY(EditAnywhere, editanywhere, EDITANYWHERE) int X;").is_empty());
}

#[test]
fn only_explicit_top_level_bare_flags_count() {
    let negatives = [
        "UPROPERTY(BlueprintReadOnly) bool Ready;",
        "UPROPERTY(BlueprintReadWrite, VisibleAnywhere) int Value;",
        "UPROPERTY(BlueprintReadOnly=true, BlueprintReadWrite) int Value;",
        "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite=false) int Value;",
        "UPROPERTY(meta=(BlueprintReadOnly, BlueprintReadWrite)) int Value;",
        "UPROPERTY(meta=(Modes=(EditAnywhere, VisibleAnywhere))) int Value;",
        "UPROPERTY(Category=\"BlueprintReadOnly,BlueprintReadWrite\") int Value;",
        "UPROPERTY(NotBlueprintReadOnly, BlueprintReadWriteExtra, FutureFlag) int Value;",
        "UPROPERTY(EditAnywhere, FutureVisibilityMode) int Value;",
        "UFUNCTION(BlueprintReadOnly, BlueprintReadWrite) void Ready();",
        "UCLASS(BlueprintReadOnly, BlueprintReadWrite) class Foo {};",
        "MY_UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) int Value;",
    ];
    for src in negatives {
        assert!(lint(src).is_empty(), "{src}");
    }
    assert_eq!(
        lint("UPROPERTY(blueprintreadonly, BLUEPRINTREADWRITE, UnknownFutureFlag) int X;").len(),
        1
    );
}

#[test]
fn conflicts_can_be_disabled_independently() {
    let src =
        "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite, EditAnywhere, VisibleAnywhere) bool Bad;";
    let parsed = parse(src);
    let config = StyleConfig {
        blueprint_access_conflicts: false,
        ..StyleConfig::default()
    };
    assert_eq!(
        check(src, &parsed, &config)[0].code,
        codes::EDIT_VISIBILITY_CONFLICT
    );
    let config = StyleConfig {
        edit_visibility_conflicts: false,
        ..StyleConfig::default()
    };
    assert_eq!(
        check(src, &parsed, &config)[0].code,
        codes::BLUEPRINT_ACCESS_CONFLICT
    );
    let config = StyleConfig {
        blueprint_access_conflicts: false,
        edit_visibility_conflicts: false,
        ..StyleConfig::default()
    };
    assert!(check(src, &parsed, &config).is_empty());
}

#[test]
fn names_are_opt_in_and_point_at_identifiers() {
    let src = "USTRUCT() struct BadStruct {};\nUENUM() enum class BadEnum : uint8 { One };\nUPROPERTY() bool Ready;";
    assert!(lint(src).is_empty());
    let ds = check(src, &parse(src), &naming());
    valid_ranges(src, &ds);
    assert_eq!(
        ds.iter()
            .map(|d| (d.code, &src[d.byte_range.clone()]))
            .collect::<Vec<_>>(),
        vec![
            (codes::STRUCT_PREFIX, "BadStruct"),
            (codes::ENUM_PREFIX, "BadEnum"),
            (codes::BOOL_PROPERTY_PREFIX, "Ready"),
        ]
    );
    assert!(ds.iter().all(|d| d.severity == Severity::Hint));
}

#[test]
fn each_naming_rule_can_be_enabled_alone() {
    let src = "USTRUCT() struct Bad {}; UENUM() enum Wrong { One }; UPROPERTY() bool Ready;";
    for (config, code) in [
        (
            StyleConfig {
                struct_prefix: true,
                ..StyleConfig::default()
            },
            codes::STRUCT_PREFIX,
        ),
        (
            StyleConfig {
                enum_prefix: true,
                ..StyleConfig::default()
            },
            codes::ENUM_PREFIX,
        ),
        (
            StyleConfig {
                bool_property_prefix: true,
                ..StyleConfig::default()
            },
            codes::BOOL_PROPERTY_PREFIX,
        ),
    ] {
        let ds = check(src, &parse(src), &config);
        assert_eq!(ds.len(), 1);
        assert_eq!(ds[0].code, code);
    }
}

#[test]
fn valid_names_and_non_bool_properties_are_ignored() {
    let src = "USTRUCT() struct FData {}; UENUM() enum class EMode { One };\n\
        UPROPERTY() bool bReady; UPROPERTY() bool bIsOK2;\n\
        UPROPERTY() int Ready; UPROPERTY() uint8 ReadyBit : 1;\n\
        UPROPERTY() Boolean ReadyAlias; UPROPERTY() TArray<bool> ReadyArray;\n\
        UPROPERTY() bool* ReadyPointer; UPROPERTY() bool& ReadyRef;\n\
        UPROPERTY() bool ReadyItems[3]; UPROPERTY() bool ReadyFunction();\n\
        struct Plain {}; enum Unreflected { Zero }; bool Enabled;";
    assert!(check(src, &parse(src), &naming()).is_empty());
}

#[test]
fn naming_rejects_missing_prefixes_empty_suffixes_and_bool_underscores() {
    for name in [
        "Ready",
        "bready",
        "b",
        "BReady",
        "bIs_ready",
        "b_Ready",
        "b1Ready",
    ] {
        let src = format!("UPROPERTY() bool {name};");
        let ds = check(&src, &parse(&src), &naming());
        assert_eq!(ds.len(), 1, "{src}");
        assert_eq!(ds[0].code, codes::BOOL_PROPERTY_PREFIX);
    }
    for src in ["USTRUCT() struct F {};", "UENUM() enum E { One };"] {
        assert_eq!(check(src, &parse(src), &naming()).len(), 1, "{src}");
    }
}

#[test]
fn comments_and_literals_do_not_create_conflicts() {
    let src = r####"
// UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool Bad;
/* UPROPERTY(EditAnywhere, VisibleAnywhere) bool Bad; */
const char* a = "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool Bad;";
const char* b = R"tag(" UPROPERTY(EditAnywhere, VisibleAnywhere) bool Bad; ")tag";
const char* c = u8R"x(" USTRUCT() struct Bad {}; ")x";
const char* d = "escaped \" UPROPERTY(EditAnywhere, VisibleAnywhere)";
const auto ch = '\'';
UPROPERTY(BlueprintReadOnly /* , BlueprintReadWrite */) bool bReady;
UPROPERTY(EditAnywhere, Category=R"x(VisibleAnywhere, " BlueprintReadOnly)x") int Value;
UPROPERTY(BlueprintReadOnly // , BlueprintReadWrite
) bool bDone;
"####;
    assert!(check(src, &parse(src), &naming()).is_empty());
    let positive = "UPROPERTY /* note */ (BlueprintReadOnly /* comment */, // newline\nBlueprintReadWrite) bool bReady;";
    assert_eq!(lint(positive).len(), 1);
}

#[test]
fn preprocessor_definitions_and_continued_comments_are_not_evidence() {
    let src = [
        "#define REFLECT UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool Bad;",
        "#define MORE BACKSLASH",
        " UPROPERTY(EditAnywhere, VisibleAnywhere) bool Wrong;",
        "// continuation BACKSLASH",
        " UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool Bad;",
        "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool bReady;",
    ]
    .join("\r\n")
    .replace("BACKSLASH", &char::from(92).to_string());
    let ds = check(&src, &parse(&src), &naming());
    assert_eq!(ds.len(), 1);
    assert_eq!(ds[0].byte_range.start, src.rfind("UPROPERTY").unwrap());
}

#[test]
fn unicode_crlf_and_multiline_macro_ranges_are_bytes() {
    let src = "// 🐧 café\r\nUSTRUCT()\r\nstruct 数据 {};\r\nUPROPERTY(\r\nBlueprintReadOnly, BlueprintReadWrite, Category=\"雪\"\r\n) bool Ready;\r\nUPROPERTY() bool bÉlan;";
    let ds = check(src, &parse(src), &naming());
    valid_ranges(src, &ds);
    assert_eq!(ds.len(), 3);
    assert_eq!(&src[ds[0].byte_range.clone()], "数据");
    assert_eq!(
        &src[ds[1].byte_range.clone()],
        "UPROPERTY(\r\nBlueprintReadOnly, BlueprintReadWrite, Category=\"雪\"\r\n)"
    );
    assert_eq!(&src[ds[2].byte_range.clone()], "Ready");
    let pos = src.find("数据").unwrap();
    assert_eq!(ds[0].byte_range, pos..pos + "数据".len());
}

#[test]
fn incomplete_or_unknown_macro_syntax_is_suppressed() {
    let cases = [
        "UPROPERTY BlueprintReadOnly, BlueprintReadWrite; bool Ready;",
        "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite bool Ready;",
        "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite, meta=(Oops]) bool Ready;",
        "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite,) bool Ready;",
        "UPROPERTY(BlueprintReadOnly,, BlueprintReadWrite) bool Ready;",
        "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite, Category=) bool Ready;",
        "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite, Category=\"unterminated) bool Ready;",
        "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite, Unknown(Anything)) bool Ready;",
        "UPROPERTY(BlueprintReadOnly, BlueprintReadWrite /* missing close) bool Ready;",
        "USTRUCT struct Bad {};",
        "UENUM enum Wrong { One };",
        "UPROPERTY bool Ready;",
    ];
    for src in cases {
        assert!(check(src, &parse(src), &naming()).is_empty(), "{src}");
    }
}

#[test]
fn naming_does_not_jump_to_later_declarations_or_guess_broken_ones() {
    let cases = [
        "USTRUCT() UNKNOWN_THING() struct Wrong {};",
        "USTRUCT() ; struct Wrong {};",
        "USTRUCT() struct Wrong {",
        "USTRUCT() struct Wrong {}",
        "UENUM() enum Wrong { One",
        "UPROPERTY() bool Ready",
        "UPROPERTY() UNKNOWN_PROPERTY() bool Ready;",
        "UPROPERTY() int Wrong; bool Ready;",
        "UPROPERTY() bool Ready = UNKNOWN_VALUE;",
        "UPROPERTY() bool Ready, Other;",
        "UPROPERTY() bool Ready();",
    ];
    for src in cases {
        assert!(check(src, &parse(src), &naming()).is_empty(), "{src}");
    }
}

#[test]
fn stale_or_invalid_parsed_ranges_and_unknown_kinds_are_safe() {
    let src = "// 雪\r\nUPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool Ready;";
    let original = parse(src);
    assert_eq!(original.symbols.len(), 1);
    for range in [
        0..0,
        3..4,
        0..usize::MAX,
        src.len()..usize::MAX,
        usize::MAX..usize::MAX,
    ] {
        let mut parsed = original.clone();
        parsed.symbols[0].byte_range = range;
        assert!(check(src, &parsed, &naming()).is_empty());
    }
    let mut parsed = original.clone();
    parsed.symbols[0].macro_name = "UNKNOWN_PROPERTY".into();
    assert!(check(src, &parsed, &naming()).is_empty());
    parsed.symbols[0].macro_name = "UPROPERTY".into();
    parsed.symbols[0].kind = SymbolKind::Function;
    assert!(check(src, &parsed, &naming()).is_empty());
    assert!(check("", &original, &naming()).is_empty());
    assert!(check(src, &ParsedFile::default(), &naming()).is_empty());
}

#[test]
fn forged_parser_specifiers_do_not_override_source_evidence() {
    let src = "UPROPERTY() bool bReady;";
    let mut parsed = parse(src);
    parsed.symbols[0].specifiers = vec![
        ue_parser::Specifier::flag("BlueprintReadOnly"),
        ue_parser::Specifier::flag("BlueprintReadWrite"),
    ];
    assert!(check(src, &parsed, &naming()).is_empty());
    parsed.symbols[0].name = "Wrong".into();
    assert!(check(src, &parsed, &naming()).is_empty());
}

#[test]
fn stable_order_and_duplicates_do_not_depend_on_symbol_order() {
    let src = "UPROPERTY(EditAnywhere, VisibleAnywhere, BlueprintReadWrite, BlueprintReadOnly) bool Ready;\r\nUSTRUCT() struct Wrong {};";
    let parsed = parse(src);
    let expected = check(src, &parsed, &naming());
    assert_eq!(expected.len(), 4);
    let mut shuffled = parsed.clone();
    shuffled.symbols.reverse();
    shuffled.symbols.extend(parsed.symbols.clone());
    for _ in 0..5 {
        assert_eq!(check(src, &shuffled, &naming()), expected);
    }
    assert_eq!(parse(src), parsed);
}

#[test]
fn truncated_headers_never_panic_and_all_emitted_ranges_are_valid() {
    let src = "// 雪\r\nUSTRUCT() struct 数据 {};\r\nUPROPERTY(BlueprintReadOnly, BlueprintReadWrite, meta=(DisplayName=\"雪\")) bool Ready;\nUENUM() enum Wrong { One };";
    let mut parser = UnrealParser::new().unwrap();
    for end in (0..=src.len()).filter(|&i| src.is_char_boundary(i)) {
        let prefix = &src[..end];
        let parsed = parser.parse(prefix);
        let ds = check(prefix, &parsed, &naming());
        valid_ranges(prefix, &ds);
        assert_eq!(ds, check(prefix, &parsed, &naming()));
    }
}

#[test]
fn unknown_wrappers_and_nested_attributes_are_not_expanded() {
    for src in [
        "UNKNOWN(UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool Ready;)",
        "UNKNOWN(USTRUCT() struct Wrong {};)",
        "[[UNKNOWN(UPROPERTY(EditAnywhere, VisibleAnywhere))]] bool Ready;",
        "USTRUCT() struct Wrong UNKNOWN {} ;",
        "UENUM() enum Wrong UNKNOWN { One };",
    ] {
        assert!(check(src, &parse(src), &naming()).is_empty(), "{src}");
    }
}

#[test]
fn normal_class_members_and_exported_types_are_checked() {
    let src = "USTRUCT() struct GAME_API Wrong : public FBase { GENERATED_BODY()\n\
        UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool Ready;\n\
        UPROPERTY() const bool Enabled = true;\n\
        UPROPERTY() bool Active : 1;\n\
        }; UENUM() enum class WrongEnum : uint8 { One };";
    let ds = check(src, &parse(src), &naming());
    valid_ranges(src, &ds);
    assert_eq!(
        ds.iter().map(|d| d.code).collect::<Vec<_>>(),
        vec![
            codes::STRUCT_PREFIX,
            codes::BLUEPRINT_ACCESS_CONFLICT,
            codes::BOOL_PROPERTY_PREFIX,
            codes::BOOL_PROPERTY_PREFIX,
            codes::BOOL_PROPERTY_PREFIX,
            codes::ENUM_PREFIX,
        ]
    );
}
