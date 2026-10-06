//! End-to-end parses of representative Unreal headers.

use ue_parser::{SymbolKind, UnrealParser};

const ACTOR_HEADER: &str = r#"
#pragma once

#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "MyActor.generated.h"

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnHealthChanged, float, NewHealth);

UENUM(BlueprintType)
enum class EWeaponState : uint8
{
    Idle    UMETA(DisplayName = "Idle"),
    Firing  UMETA(DisplayName = "Firing")
};

USTRUCT(BlueprintType)
struct FWeaponStats
{
    GENERATED_BODY()

    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Stats")
    float Damage;
};

UCLASS(Blueprintable, meta = (DisplayName = "My Actor"))
class MYGAME_API AMyActor : public AActor, public IMyInterface
{
    GENERATED_BODY()

public:
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Health",
              meta = (ClampMin = "0.0", ClampMax = "100.0"))
    float Health;

    UPROPERTY(VisibleAnywhere)
    UStaticMeshComponent* MeshComponent;

    UPROPERTY(Transient)
    TObjectPtr<AActor> Target;

    UFUNCTION(BlueprintCallable, Category = "Combat")
    void Fire();

    UFUNCTION(BlueprintPure)
    float GetHealth() const;
};
"#;

fn parse(src: &str) -> ue_parser::ParsedFile {
    UnrealParser::new().expect("grammar loads").parse(src)
}

#[test]
fn class_resolves_name_and_every_base() {
    let parsed = parse(ACTOR_HEADER);
    let class = parsed.find("AMyActor").expect("AMyActor found");
    assert_eq!(class.kind, SymbolKind::Class);
    assert_eq!(class.macro_name, "UCLASS");
    // The `MYGAME_API` export macro must not swallow the name, and the
    // base-class clause must survive — both are lost without sanitizing.
    assert_eq!(class.bases, vec!["AActor", "IMyInterface"]);
    assert!(class.has_specifier("Blueprintable"));
}

#[test]
fn nested_meta_specifier_is_not_truncated() {
    // PLUGIN_STATE_REPORT §2.4: the legacy `([^)]*)` regex stopped at the first
    // `)`, losing ClampMax and the specifiers after it.
    let parsed = parse(ACTOR_HEADER);
    let health = parsed.find("Health").expect("Health found");
    assert_eq!(health.kind, SymbolKind::Property);
    assert_eq!(health.type_name.as_deref(), Some("float"));
    assert_eq!(health.specifier_value("Category"), Some("Health"));

    let meta = health.specifier("meta").expect("meta captured").nested();
    assert_eq!(meta.len(), 2, "both clamps survive: {meta:?}");
    assert_eq!(meta[0].value.as_deref(), Some("0.0"));
    assert_eq!(meta[1].value.as_deref(), Some("100.0"));
}

#[test]
fn pointer_and_template_property_types() {
    let parsed = parse(ACTOR_HEADER);
    assert_eq!(
        parsed.find("MeshComponent").unwrap().type_name.as_deref(),
        Some("UStaticMeshComponent*")
    );
    assert_eq!(
        parsed.find("Target").unwrap().type_name.as_deref(),
        Some("TObjectPtr<AActor>")
    );
}

#[test]
fn functions_carry_return_type_and_specifiers() {
    let parsed = parse(ACTOR_HEADER);
    let fire = parsed.find("Fire").expect("Fire found");
    assert_eq!(fire.kind, SymbolKind::Function);
    assert_eq!(fire.type_name.as_deref(), Some("void"));
    assert!(fire.has_specifier("BlueprintCallable"));

    let getter = parsed.find("GetHealth").expect("GetHealth found");
    assert_eq!(getter.type_name.as_deref(), Some("float"));
    assert!(getter.has_specifier("BlueprintPure"));
}

#[test]
fn struct_enum_and_delegate_are_found() {
    let parsed = parse(ACTOR_HEADER);

    let s = parsed.find("FWeaponStats").expect("struct found");
    assert_eq!(s.kind, SymbolKind::Struct);

    // `UMETA(...)` on enumerators otherwise collapses the whole block to ERROR.
    let e = parsed.find("EWeaponState").expect("enum found");
    assert_eq!(e.kind, SymbolKind::Enum);
    assert_eq!(e.type_name.as_deref(), Some("uint8"));

    let d = parsed.find("FOnHealthChanged").expect("delegate found");
    assert_eq!(d.kind, SymbolKind::Delegate);
    assert_eq!(d.macro_name, "DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam");
}

#[test]
fn symbols_are_in_source_order_with_correct_lines() {
    let parsed = parse(ACTOR_HEADER);
    let names: Vec<&str> = parsed.symbols.iter().map(|s| s.name.as_str()).collect();
    assert_eq!(
        names,
        vec![
            "FOnHealthChanged",
            "EWeaponState",
            "FWeaponStats",
            "Damage",
            "AMyActor",
            "Health",
            "MeshComponent",
            "Target",
            "Fire",
            "GetHealth",
        ]
    );

    let lines: Vec<usize> = parsed.symbols.iter().map(|s| s.line).collect();
    assert!(
        lines.windows(2).all(|w| w[0] <= w[1]),
        "lines must be monotonic: {lines:?}"
    );

    // Line numbers must index the ORIGINAL source, not the sanitized copy.
    let health = parsed.find("Health").unwrap();
    let source_line = ACTOR_HEADER.lines().nth(health.line - 1).unwrap();
    assert!(
        source_line.contains("UPROPERTY"),
        "line {} was {source_line:?}",
        health.line
    );
}

#[test]
fn interface_header_parses() {
    let parsed = parse(
        r#"
UINTERFACE(MinimalAPI, Blueprintable)
class UMyInterface : public UInterface
{
    GENERATED_BODY()
};
"#,
    );
    let iface = parsed.find("UMyInterface").expect("interface found");
    assert_eq!(iface.kind, SymbolKind::Interface);
    assert_eq!(iface.bases, vec!["UInterface"]);
}

#[test]
fn byte_ranges_point_at_the_macro_in_the_original_source() {
    let parsed = parse(ACTOR_HEADER);
    for symbol in &parsed.symbols {
        let span = &ACTOR_HEADER[symbol.byte_range.clone()];
        assert!(
            span.starts_with(&symbol.macro_name),
            "{} range yielded {span:?}",
            symbol.name
        );
    }
}

#[test]
fn unannotated_members_are_ignored() {
    let parsed = parse(
        r#"
UCLASS()
class AThing : public AActor
{
    GENERATED_BODY()
    int32 NotReflected;
    void AlsoNotReflected();
};
"#,
    );
    assert_eq!(parsed.symbols.len(), 1);
    assert_eq!(parsed.symbols[0].name, "AThing");
}
