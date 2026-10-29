using System.Collections.Generic;
using System.Linq;
using PenguinExtention.Services;

namespace PenguinExtention.Completion
{
    /// <summary>
    /// Ready-made full lines offered when typing the start of a reflection macro or common UE statement
    /// ("UPRO" → several complete UPROPERTY(...) lines). Inserted as plain text; <see cref="Snippet.CaretBack"/>
    /// places the caret inside the first thing you normally edit (a quoted string or empty parentheses).
    /// </summary>
    internal static class UnrealSnippets
    {
        internal sealed class Snippet
        {
            public string Keyword;      // matched against what is typed
            public string Text;         // inserted text
            public string Description;
            public int CaretBack;       // characters from the end where the caret lands (0 = end)
            public string Language = "cpp"; // pack language: "cpp", "csharp", "gdscript"
            public string PackId;

            public Snippet(string keyword, string text, string description, int caretBack = 0)
            {
                Keyword = keyword; Text = text; Description = description; CaretBack = caretBack;
            }
        }

        /// <summary>C/C++ snippets from every enabled pack (e.g. Packs/unreal/snippets.json).</summary>
        public static Snippet[] All => PackService.Snippets;

        /// <summary>Snippets whose keyword matches <paramref name="typed"/> (at least 2 characters).</summary>
        public static IEnumerable<Snippet> Matching(string typed)
        {
            if (string.IsNullOrEmpty(typed) || typed.Length < 2) return Enumerable.Empty<Snippet>();
            return All.Where(s => PenguinMatcher.Score(typed, s.Keyword) >= 0 && PenguinMatcher.Score(typed, s.Keyword) <= 3);
        }
    }
}
