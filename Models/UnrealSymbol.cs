using System;
using System.Threading;

namespace PenguinExtention.Models
{
    /// <summary>
    /// Represents a single Unreal Engine symbol extracted from source code.
    /// This is the primary data transfer object between the indexer, cache, and UI layers.
    /// </summary>
    public class UnrealSymbol
    {
        public long Id { get; set; }

        /// <summary>Short name (e.g. "ACharacter", "BeginPlay", "Health").</summary>
        public string Name { get; set; }

        /// <summary>Fully-qualified name including owner (e.g. "ACharacter::BeginPlay").</summary>
        public string FullyQualifiedName { get; set; }

        public UnrealSymbolKind Kind { get; set; }

        /// <summary>Absolute path to the source file where this symbol is declared.</summary>
        public string FilePath { get; set; }

        /// <summary>1-based line number within <see cref="FilePath"/>.</summary>
        public int LineNumber { get; set; }

        /// <summary>0-based column number within <see cref="FilePath"/>.</summary>
        public int ColumnNumber { get; set; }

        /// <summary>Enclosing class name for member symbols (functions, properties). Null for top-level symbols.</summary>
        public string OwnerClass { get; set; }

        /// <summary>Full declaration text (e.g. "virtual void BeginPlay() override").</summary>
        public string Signature { get; set; }

        /// <summary>Return type for functions, variable type for properties.</summary>
        public string ReturnType { get; set; }

        /// <summary>Preceding doc-comment text, if any.</summary>
        public string Comment { get; set; }

        /// <summary>True if this symbol lives under the Engine source tree.</summary>
        public bool IsEngineSymbol { get; set; }

        /// <summary>UTC timestamp of the last indexing pass that touched this symbol.</summary>
        public DateTime LastIndexed { get; set; }

        private int _accessCount;
        /// <summary>Access-frequency counter used for hot-symbol prioritization.</summary>
        public int AccessCount
        {
            get => _accessCount;
            set => _accessCount = value;
        }

        /// <summary>Increments the access count in a thread-safe manner.</summary>
        public void IncrementAccess()
        {
            Interlocked.Increment(ref _accessCount);
        }

        // ── Computed helpers ────────────────────────────────────────

        /// <summary>Display text for completion lists and explorer: "Owner::Name" or just "Name".</summary>
        public string DisplayText =>
            !string.IsNullOrEmpty(OwnerClass) ? $"{OwnerClass}::{Name}" : Name;

        /// <summary>Human-readable kind label.</summary>
        public string KindDisplay => Kind.ToString();

        /// <summary>Icon glyph character for the Explorer UI.</summary>
        public string KindGlyph
        {
            get
            {
                switch (Kind)
                {
                    case UnrealSymbolKind.Class:     return "\uE8A5";   // Class icon
                    case UnrealSymbolKind.Struct:    return "\uE8A5";
                    case UnrealSymbolKind.Enum:      return "\uE8EF";   // List icon
                    case UnrealSymbolKind.EnumValue: return "\uE8EF";
                    case UnrealSymbolKind.Function:  return "\uE8FC";   // Function icon
                    case UnrealSymbolKind.Property:  return "\uE8F1";   // Property icon
                    case UnrealSymbolKind.Delegate:  return "\uE8FC";
                    case UnrealSymbolKind.Macro:     return "\uE943";   // Code icon
                    case UnrealSymbolKind.Interface: return "\uE8A5";
                    default:                         return "\uE8A5";
                }
            }
        }
    }
}
