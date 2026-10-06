//! Static catalog of Unreal reflection macro specifiers.
//!
//! Ported from the legacy `Completion/UnrealMacroSpecifiers.cs`. These are
//! fixed facts about the engine's macro grammar, not anything the indexer can
//! discover from a project's own headers, so they stay hard-coded.

/// One completable specifier for a reflection macro.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct MacroSpecifier {
    /// Label shown in the completion list, e.g. `EditAnywhere`.
    pub name: &'static str,
    /// One-line explanation for the completion detail and hover.
    pub description: &'static str,
    /// Text to insert when it differs from `name` — specifiers that take a
    /// value are completed with the `= ...` scaffold already in place.
    pub insert_text: Option<&'static str>,
}

impl MacroSpecifier {
    const fn flag(name: &'static str, description: &'static str) -> Self {
        MacroSpecifier {
            name,
            description,
            insert_text: None,
        }
    }

    const fn with_value(
        name: &'static str,
        description: &'static str,
        insert_text: &'static str,
    ) -> Self {
        MacroSpecifier {
            name,
            description,
            insert_text: Some(insert_text),
        }
    }

    /// What a client should actually type into the buffer.
    pub fn insertion(&self) -> &'static str {
        match self.insert_text {
            Some(text) => text,
            None => self.name,
        }
    }
}

/// Reflection macros that take a specifier list.
pub const MACRO_NAMES: [&str; 5] = ["UPROPERTY", "UFUNCTION", "UCLASS", "USTRUCT", "UENUM"];

/// Whether `name` is a reflection macro with a known specifier list.
pub fn is_macro(name: &str) -> bool {
    MACRO_NAMES.contains(&name)
}

/// Specifiers valid inside `macro_name`, or `None` for an unknown macro.
pub fn specifiers_for(macro_name: &str) -> Option<&'static [MacroSpecifier]> {
    Some(match macro_name {
        "UPROPERTY" => &UPROPERTY[..],
        "UFUNCTION" => &UFUNCTION[..],
        "UCLASS" => &UCLASS[..],
        "USTRUCT" => &USTRUCT[..],
        "UENUM" => &UENUM[..],
        _ => return None,
    })
}

/// Looks up one specifier by name within a macro, ignoring case.
pub fn lookup(macro_name: &str, specifier: &str) -> Option<&'static MacroSpecifier> {
    specifiers_for(macro_name)?
        .iter()
        .find(|s| s.name.eq_ignore_ascii_case(specifier))
}

pub const UPROPERTY: [MacroSpecifier; 26] = [
    MacroSpecifier::flag(
        "EditAnywhere",
        "Editable in the archetype and instance property windows.",
    ),
    MacroSpecifier::flag(
        "EditDefaultsOnly",
        "Editable on archetypes only, not on instances.",
    ),
    MacroSpecifier::flag(
        "EditInstanceOnly",
        "Editable on instances only, not on archetypes.",
    ),
    MacroSpecifier::flag(
        "VisibleAnywhere",
        "Visible but read-only in all property windows.",
    ),
    MacroSpecifier::flag(
        "VisibleDefaultsOnly",
        "Visible but read-only on archetypes only.",
    ),
    MacroSpecifier::flag(
        "VisibleInstanceOnly",
        "Visible but read-only on instances only.",
    ),
    MacroSpecifier::flag(
        "BlueprintReadWrite",
        "Readable and writable from Blueprint graphs.",
    ),
    MacroSpecifier::flag(
        "BlueprintReadOnly",
        "Readable but not writable from Blueprint graphs.",
    ),
    MacroSpecifier::with_value(
        "Category",
        "Groups the property under a heading in the details panel.",
        "Category = \"\"",
    ),
    MacroSpecifier::flag("Transient", "Not saved; zero-filled at load time."),
    MacroSpecifier::flag("Replicated", "Replicated over the network."),
    MacroSpecifier::with_value(
        "ReplicatedUsing",
        "Replicated, calling the named function when the value arrives.",
        "ReplicatedUsing = OnRep_",
    ),
    MacroSpecifier::flag("SaveGame", "Included in SaveGame serialization."),
    MacroSpecifier::flag("Interp", "Can be driven by a Matinee/Sequencer track."),
    // The legacy C# catalog listed NoClear twice; a duplicate completion entry
    // is a defect, not a feature, so it appears once here.
    MacroSpecifier::flag("NoClear", "Hides the clear button in the editor."),
    MacroSpecifier::flag(
        "SimpleDisplay",
        "Shown in the non-advanced details section.",
    ),
    MacroSpecifier::flag(
        "AdvancedDisplay",
        "Hidden behind the advanced details disclosure.",
    ),
    MacroSpecifier::flag("Config", "Persisted to the class's configuration file."),
    MacroSpecifier::flag(
        "GlobalConfig",
        "Persisted to config and not overridable per class.",
    ),
    MacroSpecifier::flag(
        "Instanced",
        "Each instance gets its own copy of the referenced object.",
    ),
    MacroSpecifier::flag("Export", "Exported inline rather than as a reference."),
    MacroSpecifier::flag(
        "BlueprintAssignable",
        "Multicast delegate bindable in Blueprint.",
    ),
    MacroSpecifier::flag(
        "BlueprintCallable",
        "Multicast delegate callable from Blueprint.",
    ),
    MacroSpecifier::flag(
        "BlueprintAuthorityOnly",
        "Only runs on the network authority.",
    ),
    MacroSpecifier::flag(
        "NotReplicated",
        "Skipped during replication of a replicated struct.",
    ),
    MacroSpecifier::with_value("meta", "Metadata key/value pairs.", "meta = ()"),
];

pub const UFUNCTION: [MacroSpecifier; 16] = [
    MacroSpecifier::flag("BlueprintCallable", "Callable from a Blueprint graph."),
    MacroSpecifier::flag(
        "BlueprintPure",
        "Callable from Blueprint with no execution pins.",
    ),
    MacroSpecifier::flag(
        "BlueprintNativeEvent",
        "C++ default implementation, overridable in Blueprint.",
    ),
    MacroSpecifier::flag(
        "BlueprintImplementableEvent",
        "Declared in C++, implemented in Blueprint.",
    ),
    MacroSpecifier::with_value(
        "Category",
        "Groups the function under a heading in Blueprint menus.",
        "Category = \"\"",
    ),
    MacroSpecifier::flag("Server", "Runs on the server when called from a client."),
    MacroSpecifier::flag(
        "Client",
        "Runs on the owning client when called from the server.",
    ),
    MacroSpecifier::flag(
        "NetMulticast",
        "Runs on the server and all connected clients.",
    ),
    MacroSpecifier::flag(
        "Reliable",
        "Guaranteed delivery and ordering, at a bandwidth cost.",
    ),
    MacroSpecifier::flag(
        "Unreliable",
        "May be dropped; cheap and suitable for frequent updates.",
    ),
    MacroSpecifier::flag("WithValidation", "Pairs the RPC with a _Validate function."),
    MacroSpecifier::flag(
        "BlueprintAuthorityOnly",
        "Only runs on the network authority.",
    ),
    MacroSpecifier::flag(
        "BlueprintCosmetic",
        "Cosmetic only; does not run on dedicated servers.",
    ),
    MacroSpecifier::flag("Exec", "Callable from the in-game console."),
    MacroSpecifier::flag("CallInEditor", "Exposed as a button in the details panel."),
    MacroSpecifier::with_value("meta", "Metadata key/value pairs.", "meta = ()"),
];

pub const UCLASS: [MacroSpecifier; 17] = [
    MacroSpecifier::flag("Blueprintable", "Blueprints can derive from this class."),
    MacroSpecifier::flag(
        "NotBlueprintable",
        "Blueprints cannot derive from this class.",
    ),
    MacroSpecifier::flag("BlueprintType", "Usable as a variable type in Blueprint."),
    MacroSpecifier::flag("Abstract", "Cannot be instantiated directly."),
    MacroSpecifier::flag("NotPlaceable", "Cannot be placed in a level."),
    MacroSpecifier::flag("Placeable", "Can be placed in a level."),
    MacroSpecifier::flag("Transient", "Never saved to disk."),
    MacroSpecifier::with_value(
        "Config",
        "Names the configuration file this class reads.",
        "Config = Game",
    ),
    MacroSpecifier::flag(
        "DefaultToInstanced",
        "All instances of this class are instanced.",
    ),
    MacroSpecifier::flag(
        "EditInlineNew",
        "Can be created inline from the details panel.",
    ),
    MacroSpecifier::flag("HideDropdown", "Hidden from class-picker dropdowns."),
    MacroSpecifier::with_value(
        "ShowCategories",
        "Categories to show in the details panel.",
        "ShowCategories = ()",
    ),
    MacroSpecifier::with_value(
        "HideCategories",
        "Categories to hide in the details panel.",
        "HideCategories = ()",
    ),
    MacroSpecifier::with_value(
        "ClassGroup",
        "Groups the class in the editor's component lists.",
        "ClassGroup = ()",
    ),
    MacroSpecifier::flag(
        "MinimalAPI",
        "Only the type info is exported, not every method.",
    ),
    MacroSpecifier::with_value("Within", "Restricts the valid outer class.", "Within = "),
    MacroSpecifier::with_value("meta", "Metadata key/value pairs.", "meta = ()"),
];

pub const USTRUCT: [MacroSpecifier; 7] = [
    MacroSpecifier::flag("BlueprintType", "Usable as a variable type in Blueprint."),
    MacroSpecifier::flag("NoExport", "Not exported to generated headers."),
    MacroSpecifier::flag("Atomic", "Always serialized as a single unit."),
    MacroSpecifier::flag(
        "IsAlwaysAccessible",
        "Always accessible to the reflection system.",
    ),
    MacroSpecifier::flag("HasDefaults", "Has a default constructor worth calling."),
    MacroSpecifier::flag(
        "HasNoOpConstructor",
        "Its constructor does no meaningful work.",
    ),
    MacroSpecifier::with_value("meta", "Metadata key/value pairs.", "meta = ()"),
];

pub const UENUM: [MacroSpecifier; 3] = [
    MacroSpecifier::flag("BlueprintType", "Usable as a variable type in Blueprint."),
    MacroSpecifier::flag("Flags", "Treated as a bitfield."),
    MacroSpecifier::with_value("meta", "Metadata key/value pairs.", "meta = ()"),
];

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_macro_resolves_to_a_non_empty_list() {
        for name in MACRO_NAMES {
            let list = specifiers_for(name).unwrap_or_else(|| panic!("{name} has no catalog"));
            assert!(!list.is_empty(), "{name} catalog is empty");
        }
        assert!(specifiers_for("UINTERFACE").is_none());
    }

    #[test]
    fn specifier_names_are_unique_within_each_macro() {
        // The C# catalog shipped a duplicate NoClear, which surfaced as a
        // repeated completion item. Guard against reintroducing one.
        for name in MACRO_NAMES {
            let list = specifiers_for(name).unwrap();
            for (i, spec) in list.iter().enumerate() {
                let duplicate = list[i + 1..]
                    .iter()
                    .any(|other| other.name.eq_ignore_ascii_case(spec.name));
                assert!(!duplicate, "{name} lists {} twice", spec.name);
            }
        }
    }

    #[test]
    fn value_taking_specifiers_insert_a_scaffold() {
        let category = lookup("UPROPERTY", "Category").unwrap();
        assert_eq!(category.insertion(), "Category = \"\"");

        let edit_anywhere = lookup("UPROPERTY", "EditAnywhere").unwrap();
        assert_eq!(edit_anywhere.insertion(), "EditAnywhere");
    }

    #[test]
    fn lookup_ignores_case() {
        assert!(lookup("UFUNCTION", "blueprintcallable").is_some());
        assert!(lookup("UFUNCTION", "NoSuchSpecifier").is_none());
    }
}
