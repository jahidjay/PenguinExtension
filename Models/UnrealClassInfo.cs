using System.Collections.Generic;

namespace PenguinExtention.Models
{
    /// <summary>
    /// Extended metadata for UCLASS / USTRUCT types.
    /// Stored in the <c>class_info</c> SQLite table and cached alongside the owning <see cref="UnrealSymbol"/>.
    /// </summary>
    public class UnrealClassInfo
    {
        /// <summary>Foreign key into <see cref="UnrealSymbol.Id"/>.</summary>
        public long SymbolId { get; set; }

        /// <summary>Class name (same as the corresponding symbol's Name).</summary>
        public string ClassName { get; set; }

        /// <summary>Direct base class name (e.g. "APawn" for ACharacter).</summary>
        public string BaseClass { get; set; }

        /// <summary>Unreal module the class belongs to (e.g. "Engine", "CoreUObject").</summary>
        public string Module { get; set; }

        /// <summary>Comma-separated UCLASS/USTRUCT meta specifiers (e.g. "BlueprintType, Blueprintable").</summary>
        public string MetaSpecifiers { get; set; }

        /// <summary>True if the class is declared abstract (UCLASS(Abstract) or pure-virtual).</summary>
        public bool IsAbstract { get; set; }

        /// <summary>
        /// Cached inheritance chain from this class up to the root (e.g. ["ACharacter", "APawn", "AActor", "UObject"]).
        /// Built lazily by <see cref="Services.CacheService"/>.
        /// </summary>
        public List<string> InheritanceChain { get; set; } = new List<string>();
    }
}
