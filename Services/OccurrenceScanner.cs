using System;
using System.Collections.Generic;
using PenguinExtention.Models;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Single-pass, allocation-light identifier scanner used for name-based references.
    /// Skips comments, string/char literals (including raw strings) and #include targets.
    /// Linear in input size by construction: no regex, no backtracking.
    /// </summary>
    internal static class OccurrenceScanner
    {
        /// <summary>Every occurrence of any identifier in <paramref name="names"/>.</summary>
        public static List<SymbolOccurrence> Scan(string content, HashSet<string> names)
        {
            var results = new List<SymbolOccurrence>();
            if (string.IsNullOrEmpty(content) || names == null || names.Count == 0) return results;
            Walk(content, (start, length, line, col) =>
            {
                var ident = content.Substring(start, length);
                if (names.Contains(ident)) results.Add(Make(content, ident, start, line, col));
            });
            return results;
        }

        /// <summary>Every occurrence of one identifier; used for in-document highlighting.</summary>
        public static List<SymbolOccurrence> ScanFor(string content, string name)
        {
            var results = new List<SymbolOccurrence>();
            if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(name)) return results;
            Walk(content, (start, length, line, col) =>
            {
                if (length == name.Length && string.CompareOrdinal(content, start, name, 0, length) == 0)
                    results.Add(Make(content, name, start, line, col));
            });
            return results;
        }

        /// <summary>Returns the identifier at or immediately before <paramref name="position"/>, or null.</summary>
        public static string IdentifierAt(string text, int position, out int start)
        {
            start = -1;
            if (string.IsNullOrEmpty(text)) return null;
            int i = Math.Min(Math.Max(position, 0), text.Length);
            if ((i == text.Length || !IsIdentPart(text[i])) && i > 0 && IsIdentPart(text[i - 1])) i--;
            if (i >= text.Length || !IsIdentPart(text[i])) return null;
            int s = i, e = i;
            while (s > 0 && IsIdentPart(text[s - 1])) s--;
            while (e < text.Length && IsIdentPart(text[e])) e++;
            if (!IsIdentStart(text[s])) return null;
            start = s;
            return text.Substring(s, e - s);
        }

        private static SymbolOccurrence Make(string content, string ident, int start, int line, int col)
        {
            var occ = new SymbolOccurrence { SymbolName = ident, LineNumber = line, ColumnNumber = col };
            int p = start - 1;
            while (p >= 0 && (content[p] == ' ' || content[p] == '\t')) p--;
            if (p >= 1 && content[p] == ':' && content[p - 1] == ':')
            {
                occ.Context = OccurrenceContext.Qualified;
                int q = p - 2;
                while (q >= 0 && (content[q] == ' ' || content[q] == '\t')) q--;
                int qe = q + 1;
                while (q >= 0 && IsIdentPart(content[q])) q--;
                if (qe > q + 1) occ.Qualifier = content.Substring(q + 1, qe - q - 1);
            }
            else if (p >= 0 && content[p] == '.' || p >= 1 && content[p] == '>' && content[p - 1] == '-')
            {
                occ.Context = OccurrenceContext.MemberAccess;
            }
            return occ;
        }

        private static void Walk(string s, Action<int, int, int, int> onIdentifier)
        {
            int line = 1, lineStart = 0, i = 0, n = s.Length;
            bool lineHasCode = false;

            while (i < n)
            {
                char c = s[i];

                if (c == '\n') { line++; lineStart = ++i; lineHasCode = false; continue; }
                if (c == ' ' || c == '\t' || c == '\r') { i++; continue; }

                // Comments
                if (c == '/' && i + 1 < n)
                {
                    if (s[i + 1] == '/')
                    {
                        while (i < n && s[i] != '\n') i++;
                        continue;
                    }
                    if (s[i + 1] == '*')
                    {
                        i += 2;
                        while (i < n && !(s[i] == '*' && i + 1 < n && s[i + 1] == '/'))
                        {
                            if (s[i] == '\n') { line++; lineStart = i + 1; }
                            i++;
                        }
                        i = Math.Min(n, i + 2);
                        continue;
                    }
                }

                // #include <...> / "..." — the path is not code
                if (c == '#' && !lineHasCode)
                {
                    int j = i + 1;
                    while (j < n && (s[j] == ' ' || s[j] == '\t')) j++;
                    if (string.CompareOrdinal(s, j, "include", 0, 7) == 0 || string.CompareOrdinal(s, j, "import", 0, 6) == 0)
                    {
                        while (i < n && s[i] != '\n') i++;
                        continue;
                    }
                }
                lineHasCode = true;

                // String / char literals
                if (c == '"' || c == '\'')
                {
                    // Digit separators (1'000'000) are not char literals.
                    if (c == '\'' && i > 0 && char.IsLetterOrDigit(s[i - 1]) && i + 1 < n && char.IsLetterOrDigit(s[i + 1]) && IsInNumber(s, i))
                    {
                        i++;
                        continue;
                    }
                    i++;
                    while (i < n && s[i] != c && s[i] != '\n')
                    {
                        if (s[i] == '\\' && i + 1 < n) i++;
                        i++;
                    }
                    if (i < n && s[i] == c) i++;
                    continue;
                }

                if (IsIdentStart(c))
                {
                    int start = i;
                    while (i < n && IsIdentPart(s[i])) i++;

                    // Raw string literal: [u8|u|U|L]R"delim( ... )delim"
                    if (i < n && s[i] == '"' && IsRawPrefix(s, start, i - start))
                    {
                        int open = s.IndexOf('(', i + 1);
                        if (open > 0 && open - (i + 1) <= 16)
                        {
                            var terminator = ")" + s.Substring(i + 1, open - (i + 1)) + "\"";
                            int close = s.IndexOf(terminator, open + 1, StringComparison.Ordinal);
                            int end = close < 0 ? n : close + terminator.Length;
                            for (int k = i; k < end; k++)
                                if (s[k] == '\n') { line++; lineStart = k + 1; }
                            i = end;
                            continue;
                        }
                    }

                    onIdentifier(start, i - start, line, start - lineStart);
                    continue;
                }

                if (char.IsDigit(c))
                {
                    // Skip numeric literal including suffixes (e.g. 1.0f, 0xFFu) so suffixes aren't identifiers.
                    while (i < n && (IsIdentPart(s[i]) || s[i] == '.' || s[i] == '\'')) i++;
                    continue;
                }

                i++;
            }
        }

        private static bool IsRawPrefix(string s, int start, int length)
        {
            switch (length)
            {
                case 1: return s[start] == 'R';
                case 2: return s[start + 1] == 'R' && (s[start] == 'u' || s[start] == 'U' || s[start] == 'L');
                case 3: return s[start] == 'u' && s[start + 1] == '8' && s[start + 2] == 'R';
                default: return false;
            }
        }

        private static bool IsInNumber(string s, int quote)
        {
            int k = quote - 1;
            while (k >= 0 && (char.IsLetterOrDigit(s[k]) || s[k] == '\'')) k--;
            return k + 1 < s.Length && char.IsDigit(s[k + 1]);
        }

        internal static bool IsIdentStart(char c) => c == '_' || char.IsLetter(c);
        internal static bool IsIdentPart(char c) => c == '_' || char.IsLetterOrDigit(c);
    }
}
