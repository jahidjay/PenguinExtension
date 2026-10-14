using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PenguinExtention.Models;

namespace PenguinExtention.Services
{
    /// <summary>Source text shown in the definition peek box.</summary>
    internal sealed class DefinitionSnippet
    {
        public string FilePath;
        public int Line;          // 1-based first line of Text
        public string Text;
        public bool IsBody;       // true: the implementation; false: the declaration block
        public string Title => Path.GetFileName(FilePath) + ":" + Line + (IsBody ? "" : "  (declaration)");
    }

    /// <summary>
    /// Finds the code to show for a symbol: the function body in its .cpp when one exists, otherwise the declaration
    /// with its doc comment. File I/O — call off the UI thread. Results are cached per file write time.
    /// </summary>
    internal static class DefinitionLookup
    {
        private const int MaxLines = 24;
        private static readonly ConcurrentDictionary<string, (DateTime stamp, DefinitionSnippet snippet)> Cache =
            new ConcurrentDictionary<string, (DateTime, DefinitionSnippet)>();

        public static DefinitionSnippet For(UnrealSymbol symbol)
        {
            if (symbol == null || string.IsNullOrEmpty(symbol.FilePath) || !File.Exists(symbol.FilePath)) return null;
            var key = symbol.FilePath + "|" + symbol.LineNumber + "|" + symbol.OwnerClass + "|" + symbol.Name;
            var stamp = File.GetLastWriteTimeUtc(symbol.FilePath);
            if (Cache.TryGetValue(key, out var hit) && hit.stamp == stamp) return hit.snippet;

            DefinitionSnippet result = null;
            if (symbol.Kind == UnrealSymbolKind.Function || symbol.Kind == UnrealSymbolKind.Delegate)
                result = FindBody(symbol);
            if (result == null) result = Declaration(symbol);
            if (Cache.Count > 500) Cache.Clear();
            Cache[key] = (stamp, result);
            return result;
        }

        private static DefinitionSnippet FindBody(UnrealSymbol symbol)
        {
            var candidates = new[] { HeaderSourceResolver.Partner(symbol.FilePath), symbol.FilePath }
                .Where(f => f != null && File.Exists(f)).Distinct(StringComparer.OrdinalIgnoreCase);
            var qualified = (string.IsNullOrEmpty(symbol.OwnerClass) ? "" : Regex.Escape(symbol.OwnerClass) + @"\s*::\s*") + Regex.Escape(symbol.Name);
            var names = new[] { qualified, qualified + "_Implementation" };
            foreach (var file in candidates)
            {
                string text;
                try { text = File.ReadAllText(file); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                var masked = CodeContext.Mask(text);
                foreach (var name in names)
                    foreach (Match m in Regex.Matches(masked, @"(?<![\w:])" + name + @"\s*\("))
                    {
                        int open = m.Index + m.Length - 1;
                        int close = CodeContext.MatchParen(masked, open);
                        if (close < 0) continue;
                        int k = close + 1;
                        while (k < masked.Length && (char.IsWhiteSpace(masked[k]) || char.IsLetter(masked[k]) || masked[k] == '&')) k++;
                        if (k < masked.Length && masked[k] == ':') k = masked.IndexOf('{', k); // constructor initializer list
                        if (k < 0 || k >= masked.Length || masked[k] != '{') continue;
                        // Unqualified match inside a header class: only accept when it is the symbol's own line.
                        if (string.IsNullOrEmpty(symbol.OwnerClass) == false && !m.Value.Contains("::") && !string.Equals(file, symbol.FilePath, StringComparison.OrdinalIgnoreCase)) continue;
                        int end = CodeContext.MatchBrace(masked, k);
                        int lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                        var block = text.Substring(lineStart, (end < 0 ? Math.Min(text.Length, k + 2000) : end + 1) - lineStart);
                        return new DefinitionSnippet
                        {
                            FilePath = file, Line = 1 + Count(text, '\n', lineStart), IsBody = true, Text = Trim(block)
                        };
                    }
            }
            return null;
        }

        private static DefinitionSnippet Declaration(UnrealSymbol symbol)
        {
            string[] lines;
            try { lines = File.ReadAllLines(symbol.FilePath); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
            int at = Math.Max(0, Math.Min(lines.Length - 1, symbol.LineNumber - 1));
            int first = at;
            // Include the doc comment and reflection macro above.
            while (first > 0 && first > at - 12)
            {
                var t = lines[first - 1].Trim();
                if (t.StartsWith("//") || t.StartsWith("/*") || t.StartsWith("*") || t.EndsWith("*/") || t.StartsWith("UFUNCTION") ||
                    t.StartsWith("UPROPERTY") || t.StartsWith("UCLASS") || t.StartsWith("USTRUCT") || t.StartsWith("UENUM")) first--;
                else break;
            }
            int last = at;
            bool isType = symbol.Kind == UnrealSymbolKind.Class || symbol.Kind == UnrealSymbolKind.Struct || symbol.Kind == UnrealSymbolKind.Enum;
            int stopAt = Math.Min(lines.Length - 1, at + (isType ? 14 : 8));
            while (last < stopAt && !isType && !lines[last].Contains(";") && !lines[last].Contains("{")) last++;
            if (isType) last = stopAt;
            return new DefinitionSnippet
            {
                FilePath = symbol.FilePath, Line = first + 1, IsBody = false,
                Text = Trim(string.Join("\n", lines.Skip(first).Take(last - first + 1)))
            };
        }

        private static string Trim(string block)
        {
            var lines = block.Replace("\r\n", "\n").Split('\n').Select(l => l.Replace("\t", "    ").TrimEnd()).ToList();
            int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
            var shown = lines.Take(MaxLines).Select(l => l.Length >= indent ? l.Substring(indent) : l.TrimStart());
            var text = string.Join("\n", shown);
            return lines.Count > MaxLines ? text + "\n    … (" + (lines.Count - MaxLines) + " more lines)" : text;
        }

        private static int Count(string s, char c, int end)
        {
            int n = 0;
            for (int i = 0; i < end; i++) if (s[i] == c) n++;
            return n;
        }
    }
}
