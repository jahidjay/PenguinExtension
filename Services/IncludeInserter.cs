using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PenguinExtention.Models;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Decides whether using <see cref="UnrealSymbol"/> in a file needs a new #include, and where it goes
    /// (after the last #include, before *.generated.h, after #pragma once). Pure text logic: any thread.
    /// </summary>
    internal static class IncludeInserter
    {
        internal sealed class Plan
        {
            public string IncludePath;
            /// <summary>0-based line the include is inserted before (== line count: append).</summary>
            public int Line;
            public string Reason; // set when no include is needed
            public bool Needed => Reason == null;
        }

        /// <summary>Whether <paramref name="symbol"/> needs an include in <paramref name="currentFile"/> (whose text is <paramref name="text"/>).</summary>
        public static Plan For(UnrealSymbol symbol, string currentFile, string text)
        {
            if (symbol == null || string.IsNullOrEmpty(symbol.FilePath)) return Skip("no declaring file");
            if (!HeaderSourceResolver.IsHeader(symbol.FilePath)) return Skip("declared in a source file");
            if (!string.IsNullOrEmpty(symbol.OwnerClass)) return Skip("member of " + symbol.OwnerClass + " (comes with its class)");
            if (symbol.Kind == UnrealSymbolKind.Macro || symbol.Kind == UnrealSymbolKind.EnumValue || symbol.Kind == UnrealSymbolKind.Property)
                return Skip(symbol.Kind + " symbols are not included directly");
            if (Path.GetFileName(symbol.FilePath).EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase)) return Skip("generated header");
            if (SamePath(symbol.FilePath, currentFile)) return Skip("declared in this file");
            var partner = currentFile == null ? null : HeaderSourceResolver.Partner(currentFile);
            if (SamePath(symbol.FilePath, partner)) return Skip("declared in this file's own header");
            if (IsCoreMinimal(symbol.FilePath)) return Skip("available through CoreMinimal.h");

            var fileName = Path.GetFileName(symbol.FilePath);
            if (Includes(text, fileName)) return Skip(fileName + " is already included");
            // A .cpp sees what its own header includes.
            if (partner != null && HeaderSourceResolver.IsSource(currentFile))
            {
                string partnerText = null;
                try { partnerText = File.ReadAllText(partner); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                if (partnerText != null && Includes(partnerText, fileName)) return Skip(fileName + " is included by " + Path.GetFileName(partner));
            }

            return new Plan { IncludePath = HeaderSourceResolver.IncludePathFor(symbol.FilePath, currentFile), Line = InsertLine(text) };
        }

        private static Plan Skip(string reason) => new Plan { Reason = reason };

        private static bool Includes(string text, string fileName) =>
            Regex.IsMatch(text, "#\\s*include\\s*[\"<](?:[^\">]*/)?" + Regex.Escape(fileName) + "[\">]", RegexOptions.IgnoreCase);

        /// <summary>Engine Core / CoreUObject basics that every UE file gets through CoreMinimal.h.</summary>
        private static bool IsCoreMinimal(string path)
        {
            var p = path.Replace('\\', '/');
            return p.IndexOf("/Runtime/Core/Public/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   p.IndexOf("/Runtime/CoreUObject/Public/UObject/Object.h", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   p.IndexOf("/Runtime/CoreUObject/Public/UObject/ObjectMacros.h", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>After the last #include, but before *.generated.h (which must stay last); else after #pragma once.</summary>
        public static int InsertLine(string text)
        {
            var lines = text.Split('\n');
            int insertLine = -1, generatedLine = -1, pragmaLine = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                var t = lines[i].TrimStart();
                if (t.StartsWith("#pragma once")) pragmaLine = i;
                if (!Regex.IsMatch(t, @"^#\s*include\b")) continue;
                if (t.Contains(".generated.h")) { generatedLine = i; break; }
                insertLine = i + 1;
            }
            return generatedLine >= 0 && (insertLine < 0 || insertLine > generatedLine) ? generatedLine
                 : insertLine >= 0 ? insertLine : pragmaLine >= 0 ? pragmaLine + 1 : 0;
        }

        private static bool SamePath(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
