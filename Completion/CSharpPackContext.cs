using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PenguinExtention.Services;

namespace PenguinExtention.Completion
{
    /// <summary>
    /// Regex/brace-count view of a C# file around the caret, used by the pack completion and hover sources:
    /// which class encloses the caret, its base type, whether the caret is directly in the class body, and the
    /// text a snippet or message stub expands to. Pure string logic (no editor types), so it is testable anywhere.
    /// </summary>
    internal static class CSharpPackContext
    {
        private static readonly Regex ClassDecl = new Regex(
            @"\b(?:class|struct)\s+(?<name>[A-Za-z_]\w*)(?:\s*<[^>{;]*>)?(?:\s*:\s*(?<base>[A-Za-z_][\w.]*))?",
            RegexOptions.Compiled);
        /// <summary>Line text before the typed word that still allows a member/statement snippet ("", "private void ", "[").</summary>
        private static readonly Regex LeadIn = new Regex(
            @"^\s*(?:\[\s*)?(?:(?:public|private|protected|internal|static|override|virtual|async|void|IEnumerator)\s+)*$",
            RegexOptions.Compiled);
        private const int MaxScan = 200000;

        internal struct Enclosing
        {
            public string ClassName, BaseType;
            /// <summary>1 = directly in the class body (where methods go), 2+ = inside a method or nested block.</summary>
            public int Depth;
        }

        /// <summary>The innermost class/struct whose body contains <paramref name="caret"/>, or null.</summary>
        public static Enclosing? FindEnclosing(string text, int caret)
        {
            int from = Math.Max(0, caret - MaxScan);
            var region = text.Substring(from, caret - from);
            var matches = ClassDecl.Matches(region).Cast<Match>().ToList();
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var m = matches[i];
                if (InCommentOrString(region, m.Index)) continue;
                int depth = BraceDepth(region, m.Index + m.Length, region.Length);
                if (depth <= 0) continue; // that class already closed (or its body has not started)
                var baseType = m.Groups["base"].Success ? m.Groups["base"].Value : null;
                if (baseType != null && baseType.Contains('.')) baseType = baseType.Substring(baseType.LastIndexOf('.') + 1);
                return new Enclosing { ClassName = m.Groups["name"].Value, BaseType = baseType, Depth = depth };
            }
            return null;
        }

        /// <summary>True when the text before the word on the caret's line allows a member or statement snippet.</summary>
        public static bool IsLeadIn(string linePrefix) => LeadIn.IsMatch(linePrefix ?? "") && !(linePrefix ?? "").Contains("//");

        /// <summary>Callbacks valid for a class with <paramref name="baseType"/>: its own owner's list, else the language default (MonoBehaviour).</summary>
        public static IEnumerable<PackCallback> CallbacksFor(string baseType, string language, IEnumerable<PackCallback> all)
        {
            var list = all.Where(c => c.Language == language).ToList();
            var owner = baseType != null && list.Any(c => c.Owner == baseType) ? baseType : PackService.DefaultOwner(language);
            return list.Where(c => c.Owner == owner);
        }

        /// <summary>Names of methods already declared in the file (so Update is not offered twice).</summary>
        public static HashSet<string> DeclaredMethods(string text)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in Regex.Matches(text, @"\b(?:void|IEnumerator|bool|async\s+\w+)\s+([A-Za-z_]\w*)\s*\("))
                set.Add(m.Groups[1].Value);
            return set;
        }

        /// <summary>
        /// Expands a snippet/stub for insertion: $CLASS → class name, extra lines get the line's indentation,
        /// "\n" becomes the buffer's newline, leading 4-space groups become tabs when the editor uses tabs.
        /// Returns the text and the caret offset ($0, else end minus <paramref name="caretBack"/>).
        /// </summary>
        public static string Expand(string template, string className, string indent, string newline, bool useTabs, int caretBack, out int caret)
        {
            var text = template.Replace("$CLASS", string.IsNullOrEmpty(className) ? "MyClass" : className).Replace("\r\n", "\n");
            var lines = text.Split('\n');
            var sb = new StringBuilder();
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (useTabs)
                {
                    int spaces = 0;
                    while (spaces < line.Length && line[spaces] == ' ') spaces++;
                    line = new string('\t', spaces / 4) + new string(' ', spaces % 4) + line.Substring(spaces);
                }
                if (i > 0) sb.Append(newline).Append(line.Length == 0 ? "" : indent);
                sb.Append(line);
            }
            var result = sb.ToString();
            int marker = result.IndexOf("$0", StringComparison.Ordinal);
            if (marker >= 0) { caret = marker; return result.Remove(marker, 2); }
            caret = Math.Max(0, result.Length - Math.Max(0, caretBack));
            return result;
        }

        /// <summary>Net '{' minus '}' between two offsets, skipping comments, strings and char literals.</summary>
        internal static int BraceDepth(string s, int start, int end)
        {
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                char c = s[i];
                char next = i + 1 < end ? s[i + 1] : '\0';
                if (c == '/' && next == '/') { while (i < end && s[i] != '\n') i++; continue; }
                if (c == '/' && next == '*') { i += 2; while (i + 1 < end && !(s[i] == '*' && s[i + 1] == '/')) i++; i++; continue; }
                if (c == '"' || c == '\'')
                {
                    bool verbatim = c == '"' && i > 0 && s[i - 1] == '@';
                    char quote = c;
                    for (i++; i < end; i++)
                    {
                        if (verbatim) { if (s[i] == '"') { if (i + 1 < end && s[i + 1] == '"') { i++; continue; } break; } }
                        else if (s[i] == '\\') i++;
                        else if (s[i] == quote || s[i] == '\n') break;
                    }
                    continue;
                }
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            return depth;
        }

        private static bool InCommentOrString(string s, int index)
        {
            int lineStart = s.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
            var before = s.Substring(lineStart, index - lineStart);
            return before.Contains("//") || before.Count(ch => ch == '"') % 2 == 1;
        }
    }
}
