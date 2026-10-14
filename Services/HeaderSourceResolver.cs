using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Finds the header for a source file and vice versa, using Unreal module layout conventions
    /// (same folder, then Public/Classes ↔ Private, then anywhere in the project file index).
    /// Performs file-system probes: call off the UI thread.
    /// </summary>
    internal static class HeaderSourceResolver
    {
        private static readonly string[] HeaderExts = { ".h", ".hpp", ".hh", ".hxx", ".inl" };
        private static readonly string[] SourceExts = { ".cpp", ".cc", ".cxx", ".c" };

        public static bool IsHeader(string path) => HeaderExts.Contains(Path.GetExtension(path ?? ""), StringComparer.OrdinalIgnoreCase);
        public static bool IsSource(string path) => SourceExts.Contains(Path.GetExtension(path ?? ""), StringComparer.OrdinalIgnoreCase);

        public static string Partner(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string[] targets = IsHeader(path) ? SourceExts : IsSource(path) ? HeaderExts : null;
            if (targets == null) return null;

            var dir = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            if (stem.EndsWith(".generated", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(0, stem.Length - ".generated".Length);

            foreach (var candidateDir in CandidateDirectories(dir))
                foreach (var ext in targets)
                {
                    var candidate = Path.Combine(candidateDir, stem + ext);
                    if (File.Exists(candidate)) return candidate;
                }

            // Fallback: any file with the same stem in the project, closest folder first.
            return SolutionFileIndex.Files
                .Where(f => targets.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) &&
                            string.Equals(Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => CommonPrefixLength(f, path))
                .FirstOrDefault();
        }

        /// <summary>Same folder, then the mirrored folder with Public/Classes swapped for Private (and back).</summary>
        private static IEnumerable<string> CandidateDirectories(string dir)
        {
            yield return dir;
            var sep = Path.DirectorySeparatorChar.ToString();
            var swaps = new[] { ("Public", "Private"), ("Classes", "Private"), ("Private", "Public"), ("Private", "Classes") };
            foreach (var (from, to) in swaps)
            {
                var token = sep + from + sep;
                var padded = dir + sep;
                int idx = padded.LastIndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0) yield return (padded.Substring(0, idx) + sep + to + sep + padded.Substring(idx + token.Length)).TrimEnd(Path.DirectorySeparatorChar);
                else if (padded.EndsWith(sep + from + sep, StringComparison.OrdinalIgnoreCase))
                    yield return Path.Combine(Path.GetDirectoryName(dir), to);
            }
            // Flat module layouts keep the header one level up from Private/.
            var parent = Path.GetDirectoryName(dir);
            if (parent != null) yield return parent;
        }

        private static int CommonPrefixLength(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
            return i;
        }

        /// <summary>
        /// Include path for <paramref name="header"/> as UBT resolves it: relative to the module's Public/Classes
        /// folder (or the module root for Private headers included in-module).
        /// </summary>
        public static string IncludePathFor(string header, string includingFile)
        {
            var parts = Path.GetFullPath(header).Split(Path.DirectorySeparatorChar);
            for (int i = parts.Length - 2; i >= 0; i--)
            {
                if (parts[i].Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                    parts[i].Equals("Classes", StringComparison.OrdinalIgnoreCase) ||
                    parts[i].Equals("Private", StringComparison.OrdinalIgnoreCase))
                    return string.Join("/", parts.Skip(i + 1));
            }
            // No module layout: path relative to the including file's folder.
            if (!string.IsNullOrEmpty(includingFile))
            {
                var from = new Uri(Path.GetDirectoryName(Path.GetFullPath(includingFile)) + Path.DirectorySeparatorChar);
                return Uri.UnescapeDataString(from.MakeRelativeUri(new Uri(Path.GetFullPath(header))).ToString());
            }
            return Path.GetFileName(header);
        }
    }
}
