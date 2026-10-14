using System.Collections.Generic;

namespace PenguinExtention.Completion
{
    /// <summary>
    /// Static lookup of valid Unreal Engine macro specifiers.
    /// ponytail: one file, flat arrays, no over-engineering.
    /// </summary>
    internal static class UnrealMacroSpecifiers
    {
        internal sealed class Specifier
        {
            public string Name { get; }
            public string Description { get; }
            /// <summary>Text to insert. Null means insert Name as-is.</summary>
            public string InsertText { get; }

            public Specifier(string name, string description, string insertText = null)
            {
                Name = name;
                Description = description;
                InsertText = insertText;
            }
        }

        // ── UPROPERTY specifiers ────────────────────────────────────

        public static readonly Specifier[] UProperty =
        {
            new Specifier("EditAnywhere",         "Property can be edited in the Details panel on archetypes and instances."),
            new Specifier("EditDefaultsOnly",     "Property can only be edited on archetypes (class defaults)."),
            new Specifier("EditInstanceOnly",     "Property can only be edited on instances, not archetypes."),
            new Specifier("VisibleAnywhere",      "Property is visible but not editable in the Details panel."),
            new Specifier("VisibleDefaultsOnly",  "Property is visible on archetypes only, not editable."),
            new Specifier("VisibleInstanceOnly",  "Property is visible on instances only, not editable."),
            new Specifier("BlueprintReadWrite",   "Property can be read and written from Blueprints."),
            new Specifier("BlueprintReadOnly",    "Property can be read from Blueprints but not modified."),
            new Specifier("Category",             "Groups the property under a category in the Details panel.", "Category = \"\""),
            new Specifier("Transient",            "Property is not serialized; reset to default on load."),
            new Specifier("Replicated",           "Property is replicated over the network."),
            new Specifier("ReplicatedUsing",      "Replicated with a notification function.", "ReplicatedUsing = OnRep_"),
            new Specifier("SaveGame",             "Property is included in save game serialization."),
            new Specifier("Interp",               "Property can be driven by Matinee/Sequencer tracks."),
            new Specifier("NoClear",              "Prevents the property from being set to None in the editor."),
            new Specifier("SimpleDisplay",        "Property always appears in the simplified Details view."),
            new Specifier("AdvancedDisplay",      "Property is hidden in the Details panel unless Advanced is toggled."),
            new Specifier("Config",               "Value can be set in .ini config files."),
            new Specifier("GlobalConfig",         "Works like Config but value is shared across all instances."),
            new Specifier("Instanced",            "Object reference is instanced per-outer (deep copy)."),
            new Specifier("Export",               "Object is exported as a subobject when copy-pasting."),
            new Specifier("NoClear",              "Prevents clearing object references in the editor."),
            new Specifier("BlueprintAssignable",  "Multicast delegate can be bound in Blueprints."),
            new Specifier("BlueprintCallable",    "Delegate can be called (broadcast) from Blueprints."),
            new Specifier("BlueprintAuthorityOnly","Multicast delegate only fires on the server."),
            new Specifier("NotReplicated",        "Skips replication for a struct member."),
            new Specifier("meta",                 "Metadata specifiers block.", "meta = ()"),
        };

        // ── UFUNCTION specifiers ────────────────────────────────────

        public static readonly Specifier[] UFunction =
        {
            new Specifier("BlueprintCallable",           "Function can be called from a Blueprint graph."),
            new Specifier("BlueprintPure",               "Function has no side effects and no exec pins."),
            new Specifier("BlueprintNativeEvent",        "C++ provides default implementation; Blueprint can override."),
            new Specifier("BlueprintImplementableEvent", "No C++ body; must be implemented in Blueprint."),
            new Specifier("Category",                    "Groups the function in Blueprint menus.", "Category = \"\""),
            new Specifier("Server",                      "Function is executed on the server (RPC)."),
            new Specifier("Client",                      "Function is executed on the owning client (RPC)."),
            new Specifier("NetMulticast",                "Function is executed on server and all clients (RPC)."),
            new Specifier("Reliable",                    "RPC is guaranteed to arrive (use sparingly)."),
            new Specifier("Unreliable",                  "RPC may be dropped under bandwidth pressure."),
            new Specifier("WithValidation",              "RPC requires a validation function (_Validate)."),
            new Specifier("BlueprintAuthorityOnly",      "Only callable on the server from Blueprints."),
            new Specifier("BlueprintCosmetic",           "Function is cosmetic and runs on all clients."),
            new Specifier("Exec",                        "Function can be called from the console command line."),
            new Specifier("CallInEditor",                "Function appears as a button in the Details panel."),
            new Specifier("meta",                        "Metadata specifiers block.", "meta = ()"),
        };

        // ── UCLASS specifiers ───────────────────────────────────────

        public static readonly Specifier[] UClass =
        {
            new Specifier("Blueprintable",           "Class can be used as a base for Blueprints."),
            new Specifier("NotBlueprintable",        "Class cannot be used as a Blueprint base."),
            new Specifier("BlueprintType",           "Class can be used as a variable type in Blueprints."),
            new Specifier("Abstract",                "Class cannot be instantiated directly."),
            new Specifier("NotPlaceable",            "Class cannot be placed in a level via the editor."),
            new Specifier("Placeable",               "Overrides inherited NotPlaceable."),
            new Specifier("Transient",               "Objects of this class are never saved to disk."),
            new Specifier("Config",                  "Class can read values from a config (.ini) file.", "Config = Game"),
            new Specifier("DefaultToInstanced",      "All instances are instanced (deep-copied)."),
            new Specifier("EditInlineNew",           "Objects can be created inline in the property editor."),
            new Specifier("HideDropdown",            "Hides this class from property editor dropdowns."),
            new Specifier("ShowCategories",          "Overrides inherited HideCategories.", "ShowCategories = ()"),
            new Specifier("HideCategories",          "Hides specified categories in the editor.", "HideCategories = ()"),
            new Specifier("ClassGroup",              "Groups the class in the editor class viewer.", "ClassGroup = ()"),
            new Specifier("MinimalAPI",              "Only the type is exported; no functions."),
            new Specifier("Within",                  "Class can only exist as a subobject of the named outer class.", "Within = "),
            new Specifier("meta",                    "Metadata specifiers block.", "meta = ()"),
        };

        // ── USTRUCT specifiers ──────────────────────────────────────

        public static readonly Specifier[] UStruct =
        {
            new Specifier("BlueprintType",   "Struct can be used as a variable type in Blueprints."),
            new Specifier("NoExport",        "Struct is not auto-generated in the .generated.h header."),
            new Specifier("Atomic",          "Struct is always serialized as a single unit."),
            new Specifier("IsAlwaysAccessible", "Struct is always accessible for Blueprint use."),
            new Specifier("HasDefaults",     "Struct supports default value initialization."),
            new Specifier("HasNoOpConstructor","Struct has no user-defined constructor."),
            new Specifier("meta",            "Metadata specifiers block.", "meta = ()"),
        };

        // ── UENUM specifiers ────────────────────────────────────────

        public static readonly Specifier[] UEnum =
        {
            new Specifier("BlueprintType", "Enum can be used as a variable type in Blueprints."),
            new Specifier("Flags",         "Enum values can be combined as a bitmask."),
            new Specifier("meta",          "Metadata specifiers block.", "meta = ()"),
        };

        /// <summary>Returns the specifier list for a given macro name, or null if unknown.</summary>
        // ── meta = ( … ) keys (common, from the public UE metadata-specifier docs) ──

        public static readonly Specifier[] Meta =
        {
            new Specifier("DisplayName",          "Name shown in the editor and Blueprint instead of the C++ name.", "DisplayName = \"\""),
            new Specifier("ToolTip",              "Overrides the tooltip taken from the doc comment.", "ToolTip = \"\""),
            new Specifier("ClampMin",             "Minimum value the editor allows for a numeric property.", "ClampMin = \"\""),
            new Specifier("ClampMax",             "Maximum value the editor allows for a numeric property.", "ClampMax = \"\""),
            new Specifier("UIMin",                "Minimum value of the editor slider.", "UIMin = \"\""),
            new Specifier("UIMax",                "Maximum value of the editor slider.", "UIMax = \"\""),
            new Specifier("Units",                "Display unit, e.g. \"cm\", \"s\", \"deg\".", "Units = \"\""),
            new Specifier("EditCondition",        "Property is editable only when the expression is true.", "EditCondition = \"\""),
            new Specifier("EditConditionHides",   "Hide (instead of disable) the property when EditCondition is false."),
            new Specifier("InlineEditConditionToggle", "Bool shown as an inline checkbox next to the property it controls."),
            new Specifier("AllowPrivateAccess",   "Lets Blueprint access a private/protected C++ property."),
            new Specifier("ExposeOnSpawn",        "Shows the property as a pin on SpawnActor / Construct Object nodes."),
            new Specifier("MakeEditWidget",       "Shows a 3D widget for a vector/transform property in the viewport."),
            new Specifier("BindWidget",           "UMG: bind this property to the widget with the same name."),
            new Specifier("BindWidgetOptional",   "UMG: like BindWidget, but no compile error when missing."),
            new Specifier("AllowedClasses",       "Restricts asset pickers to these classes.", "AllowedClasses = \"\""),
            new Specifier("MustImplement",        "Class pickers only list classes implementing this interface.", "MustImplement = \"\""),
            new Specifier("TitleProperty",        "Array of structs: property used as each element's title.", "TitleProperty = \"\""),
            new Specifier("GetOptions",           "Function returning the dropdown options for an FName/FString.", "GetOptions = \"\""),
            new Specifier("BlueprintProtected",   "Callable only from Blueprints of this class or subclasses."),
            new Specifier("DefaultToSelf",        "Pin defaults to the calling Blueprint's self.", "DefaultToSelf = \"\""),
            new Specifier("HidePin",              "Hides the named parameter pin.", "HidePin = \"\""),
            new Specifier("WorldContext",         "Parameter that receives the world context automatically.", "WorldContext = \"\""),
            new Specifier("Keywords",             "Extra search keywords for the Blueprint action menu.", "Keywords = \"\""),
            new Specifier("CompactNodeTitle",     "Show the node in compact form with this title.", "CompactNodeTitle = \"\""),
            new Specifier("ExpandEnumAsExecs",    "Turns an enum parameter into multiple exec pins.", "ExpandEnumAsExecs = \"\""),
            new Specifier("DeprecatedFunction",   "Marks a Blueprint function as deprecated."),
            new Specifier("DeprecationMessage",   "Message shown for deprecated items.", "DeprecationMessage = \"\""),
            new Specifier("BlueprintSpawnableComponent", "UCLASS: component can be added in the Blueprint editor."),
            new Specifier("IsBlueprintBase",      "UCLASS: may be used as a Blueprint parent class.", "IsBlueprintBase = \"true\""),
        };

        public static Specifier[] GetSpecifiers(string macroName)
        {
            switch (macroName)
            {
                case "meta":       return Meta;
                case "UPROPERTY":  return UProperty;
                case "UFUNCTION":  return UFunction;
                case "UCLASS":     return UClass;
                case "USTRUCT":    return UStruct;
                case "UENUM":      return UEnum;
                default:           return null;
            }
        }

        /// <summary>All known macro names that support specifiers.</summary>
        public static readonly HashSet<string> MacroNames = new HashSet<string>
        {
            "UPROPERTY", "UFUNCTION", "UCLASS", "USTRUCT", "UENUM"
        };
    }
}
