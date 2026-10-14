using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Background-built list of project files for Open File in Solution and header/source lookups.
    /// Covers the project's Source, Config, Plugins and Shaders folders; skips build output and generated headers.
    /// Rebuilt lazily when stale (30 s) — cheap compared to opening a dialog, and needs no extra watchers.
    /// </summary>
    internal static class SolutionFileIndex
    {
        private static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".h", ".hpp", ".hh", ".hxx", ".inl", ".cpp", ".cc", ".cxx", ".c", ".cs", ".ini", ".usf", ".ush", ".hlsl", ".glsl", ".vert", ".frag",
            ".uplugin", ".uproject", ".json", ".txt", ".md", ".cmake", ".vcxproj", ".props", ".xml", ".lua"
        };
        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Intermediate", "Binaries", "Saved", "DerivedDataCache", ".git", ".vs", "Content",
            "x64", "x86", "Win32", "ARM64", "Debug", "Release", "obj", "bin", "out", "build", "node_modules", "packages", "vcpkg_installed"
        };

        private static volatile string[] _files = new string[0];
        private static string _root;
        private static DateTime _builtAt = DateTime.MinValue;
        private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public static IReadOnlyList<string> Files => _files;

        /// <summary>Returns a fresh list, rebuilding on a background thread when stale or the project changed.</summary>
        public static async Task<IReadOnlyList<string>> GetAsync(CancellationToken token)
        {
            var root = UnrealProjectDetector.Instance?.ProjectRoot;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return _files;
            if (root == _root && DateTime.UtcNow - _builtAt < TimeSpan.FromSeconds(30)) return _files;

            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (root == _root && DateTime.UtcNow - _builtAt < TimeSpan.FromSeconds(30)) return _files;
                _files = await Task.Run(() => Enumerate(root, token).ToArray(), token).ConfigureAwait(false);
                _root = root;
                _builtAt = DateTime.UtcNow;
                return _files;
            }
            finally
            {
                _gate.Release();
            }
        }

        private static IEnumerable<string> Enumerate(string root, CancellationToken token)
        {
            bool unreal = UnrealProjectDetector.Instance?.IsUnrealProject == true;
            if (unreal) foreach (var f in SafeFiles(root)) if (Extensions.Contains(Path.GetExtension(f))) yield return f;
            // Unreal: known top-level folders. Plain C/C++: the whole solution folder.
            foreach (var top in unreal ? new[] { "Source", "Config", "Plugins", "Shaders" } : new[] { "" })
            {
                var stack = new Stack<string>();
                var start = Path.Combine(root, top);
                if (Directory.Exists(start)) stack.Push(start);
                while (stack.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var dir = stack.Pop();
                    foreach (var f in SafeFiles(dir))
                    {
                        if (!Extensions.Contains(Path.GetExtension(f))) continue;
                        if (f.EndsWith(".generated.h", StringComparison.OrdinalIgnoreCase)) continue;
                        yield return f;
                    }
                    string[] subdirs;
                    try { subdirs = Directory.GetDirectories(dir); } catch { continue; }
                    foreach (var d in subdirs) if (!SkipDirs.Contains(Path.GetFileName(d))) stack.Push(d);
                }
            }
        }

        private static string[] SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir); } catch { return new string[0]; }
        }

        /// <summary>
        /// Fuzzy score for <paramref name="query"/> against a file name (higher is better, 0 = no match):
        /// prefix, then substring, then camel-hump/acronym, with multiple space-separated terms all required.
        /// </summary>
        public static int Score(string query, string fileName)
        {
            if (string.IsNullOrWhiteSpace(query)) return 1;
            int total = 0;
            foreach (var term in query.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int s = ScoreTerm(term, fileName);
                if (s == 0) return 0;
                total += s;
            }
            return total;
        }

        private static int ScoreTerm(string term, string name)
        {
            if (name.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 1000 - name.Length;
            int idx = name.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return 600 - idx - name.Length;
            return AcronymIndex.Matches(term, name) ? 300 - name.Length : 0;
        }
    }
}
