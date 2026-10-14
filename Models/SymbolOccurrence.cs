namespace PenguinExtention.Models
{
    /// <summary>
    /// How an identifier occurrence was written. Integer values are persisted in SQLite — do not reorder.
    /// </summary>
    public enum OccurrenceContext
    {
        /// <summary>Unqualified use; may refer to a different symbol with the same name.</summary>
        Plain = 0,
        /// <summary>The symbol's own declaration line.</summary>
        Declaration = 1,
        /// <summary>Written as <c>Owner::Name</c>.</summary>
        Qualified = 2,
        /// <summary>Written after <c>.</c> or <c>-&gt;</c>.</summary>
        MemberAccess = 3
    }

    /// <summary>
    /// A name-based reference candidate (not a semantic reference — there is no C++ AST).
    /// </summary>
    public class SymbolOccurrence
    {
        public string SymbolName { get; set; }
        public string FilePath { get; set; }

        /// <summary>1-based line number.</summary>
        public int LineNumber { get; set; }

        /// <summary>0-based column of the first character of the identifier.</summary>
        public int ColumnNumber { get; set; }

        public OccurrenceContext Context { get; set; }

        /// <summary>Owner written before <c>::</c> for <see cref="OccurrenceContext.Qualified"/> hits, if known.</summary>
        public string Qualifier { get; set; }

        /// <summary>Source line text, filled in for display only.</summary>
        public string LineText { get; set; }

        /// <summary>
        /// True when the hit is likely to be the requested symbol: its declaration, or qualified with the expected owner.
        /// Rename previews leave uncertain hits unchecked by default.
        /// </summary>
        public bool IsCertain { get; set; }
    }
}
