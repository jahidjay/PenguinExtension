using System.Collections.Generic;
using PenguinExtention.Services;

namespace PenguinExtention.Completion
{
    /// <summary>
    /// Unreal macro specifiers. The data lives in packs (Packs/unreal/specifiers.json) and is served by PackService.
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

        /// <summary>Specifier list for a macro (or "meta"), from the enabled packs (Packs/unreal/specifiers.json). Null if unknown.</summary>
        public static Specifier[] GetSpecifiers(string macroName) => PackService.GetSpecifiers(macroName);

        /// <summary>Macro names that take specifiers (UPROPERTY, UFUNCTION, …) in the enabled packs.</summary>
        public static HashSet<string> MacroNames => PackService.MacroNames;
    }
}
