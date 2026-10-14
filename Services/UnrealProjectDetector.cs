using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Detects whether the current VS solution is an Unreal Engine project and locates
    /// key paths (project source, engine source, .uproject file).
    /// </summary>
    internal sealed class UnrealProjectDetector
    {
        private static UnrealProjectDetector _instance;
        public static UnrealProjectDetector Instance => _instance;

        public bool IsUnrealProject { get; private set; }
        public string UProjectFilePath { get; private set; }
        public string ProjectRoot { get; private set; }
        public string ProjectSourceRoot { get; private set; }
        public string EngineSourceRoot { get; private set; }
        public string SolutionDirectory { get; private set; }
        public string EngineVersion { get; private set; }

        public static void CreateInstance() => _instance = new UnrealProjectDetector();

        /// <summary>
        /// Scans the solution directory and parent directories for Unreal Engine indicators.
        /// Must be called on a background thread (uses file I/O).
        /// </summary>
        public async Task DetectAsync(AsyncPackage package, string engineRootOverride, string extraIncludeDirectories = null)
        {
            await Task.Yield(); // ensure we're off the UI thread

            SolutionDirectory = await GetSolutionDirectoryAsync(package).ConfigureAwait(false);
            if (string.IsNullOrEmpty(SolutionDirectory))
                return;

            await TaskScheduler.Default;
            // Step 1: Find .uproject file
            UProjectFilePath = FindUProjectFile(SolutionDirectory);
            if (string.IsNullOrEmpty(UProjectFilePath))
            {
                // Try parent directories (UE solutions are often in Intermediate/ProjectFiles)
                var dir = Directory.GetParent(SolutionDirectory);
                for (int i = 0; i < 4 && dir != null; i++)
                {
                    UProjectFilePath = FindUProjectFile(dir.FullName);
                    if (!string.IsNullOrEmpty(UProjectFilePath))
                        break;
                    dir = dir.Parent;
                }
            }

            if (string.IsNullOrEmpty(UProjectFilePath))
            {
                DetectPlainCpp(extraIncludeDirectories);
                return;
            }

            IsUnrealProject = true;
            ProjectRoot = Path.GetDirectoryName(UProjectFilePath);
            ProjectSourceRoot = Path.Combine(ProjectRoot, "Source");

            // Step 2: Find engine source
            EngineSourceRoot = ResolveEngineSource(engineRootOverride);
        }

        // ── Plain C/C++ solutions (SDL2 and other engine-less projects) ─

        /// <summary>True for a non-Unreal solution containing C/C++ projects.</summary>
        public bool IsPlainCppProject { get; private set; }

        /// <summary>Library include directories outside the solution (e.g. SDL2\include), indexed as external symbols.</summary>
        public List<string> ExternalIncludeRoots { get; } = new List<string>();

        /// <summary>True when the extension should run (Unreal or plain C++).</summary>
        public bool IsSupportedProject => IsUnrealProject || IsPlainCppProject;

        private void DetectPlainCpp(string extraIncludeDirectories)
        {
            var projects = SafeEnumerate(SolutionDirectory, "*.vcxproj", 4).ToList();
            bool hasSources = projects.Count > 0 || SafeEnumerate(SolutionDirectory, "*.cpp", 3).Any() || SafeEnumerate(SolutionDirectory, "*.c", 3).Any();
            if (!hasSources) return;

            IsPlainCppProject = true;
            ProjectRoot = SolutionDirectory;
            ProjectSourceRoot = SolutionDirectory;

            var roots = new List<string>();
            foreach (var proj in projects) roots.AddRange(IncludeDirectoriesOf(proj));
            if (!string.IsNullOrWhiteSpace(extraIncludeDirectories))
                roots.AddRange(extraIncludeDirectories.Split(';').Select(p => Environment.ExpandEnvironmentVariables(p.Trim())));

            var solutionPrefix = SolutionDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var root in roots.Where(r => r.Length > 0).Select(SafeFullPath).Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(root)) continue;
                if ((root + Path.DirectorySeparatorChar).StartsWith(solutionPrefix, StringComparison.OrdinalIgnoreCase)) continue; // already indexed as project
                if (IsSystemIncludeRoot(root)) continue; // Windows SDK / MSVC STL: huge and not useful as symbols
                ExternalIncludeRoots.Add(root);
            }
        }

        /// <summary>AdditionalIncludeDirectories of every configuration, with MSBuild macros and %ENV% expanded.</summary>
        internal static IEnumerable<string> IncludeDirectoriesOf(string vcxproj)
        {
            string xml;
            try { xml = File.ReadAllText(vcxproj); } catch { yield break; }
            var projectDir = Path.GetDirectoryName(vcxproj) + Path.DirectorySeparatorChar;
            var solutionDir = FindSolutionDirFor(projectDir);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(xml,
                @"<(?:AdditionalIncludeDirectories|IncludePath|ExternalIncludePath)>([^<]*)</", System.Text.RegularExpressions.RegexOptions.Compiled))
            {
                foreach (var raw in m.Groups[1].Value.Split(';'))
                {
                    var p = raw.Trim();
                    if (p.Length == 0 || p.StartsWith("%(") || p.Contains("$(VC_") || p.Contains("$(WindowsSDK") || p.Contains("$(IncludePath)") || p.Contains("$(ExternalIncludePath)")) continue;
                    p = p.Replace("$(ProjectDir)", projectDir).Replace("$(MSBuildProjectDirectory)", projectDir.TrimEnd('\\'))
                         .Replace("$(SolutionDir)", solutionDir).Replace("$(Platform)", "x64").Replace("$(Configuration)", "Debug");
                    p = System.Text.RegularExpressions.Regex.Replace(p, @"\$\((\w+)\)", mm => Environment.GetEnvironmentVariable(mm.Groups[1].Value) ?? mm.Value);
                    p = Environment.ExpandEnvironmentVariables(p);
                    if (p.Contains("$(")) continue;
                    yield return Path.IsPathRooted(p) ? p : Path.Combine(projectDir, p);
                }
            }
        }

        private static string FindSolutionDirFor(string projectDir)
        {
            var dir = new DirectoryInfo(projectDir);
            for (int i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
                if (SafeEnumerate(dir.FullName, "*.sln", 1).Any()) return dir.FullName.TrimEnd('\\') + "\\";
            return projectDir;
        }

        private static bool IsSystemIncludeRoot(string root) =>
            root.IndexOf(@"\Windows Kits\", StringComparison.OrdinalIgnoreCase) >= 0 ||
            root.IndexOf(@"\Microsoft Visual Studio\", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string SafeFullPath(string p)
        {
            try { return Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar); } catch { return null; }
        }

        private static IEnumerable<string> SafeEnumerate(string root, string pattern, int maxDepth)
        {
            var stack = new Stack<(string dir, int depth)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (dir, depth) = stack.Pop();
                string[] files = null, dirs = null;
                try { files = Directory.GetFiles(dir, pattern); dirs = depth < maxDepth ? Directory.GetDirectories(dir) : new string[0]; } catch { }
                if (files != null) foreach (var f in files) yield return f;
                if (dirs != null)
                    foreach (var d in dirs)
                    {
                        var name = Path.GetFileName(d);
                        if (name.StartsWith(".") || name == "node_modules" || name == "x64" || name == "Debug" || name == "Release") continue;
                        stack.Push((d, depth + 1));
                    }
            }
        }

        // ── Engine source resolution ────────────────────────────────

        private string ResolveEngineSource(string engineRootOverride)
        {
            // Priority 1: User override from options
            if (!string.IsNullOrWhiteSpace(engineRootOverride))
            {
                var overrideSrc = Path.Combine(engineRootOverride, "Engine", "Source");
                if (Directory.Exists(overrideSrc))
                    return overrideSrc;
            }

            // Priority 2: Walk up from project to find Engine/Source sibling
            var engineFromProject = FindEngineByWalkingUp(ProjectRoot);
            if (engineFromProject != null)
                return engineFromProject;

            // Priority 3: Read .uproject → EngineAssociation → registry
            var engineFromRegistry = FindEngineFromUProject();
            if (engineFromRegistry != null)
                return engineFromRegistry;

            return null;
        }

        private string FindEngineByWalkingUp(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            for (int i = 0; i < 6 && dir != null; i++)
            {
                var candidate = Path.Combine(dir.FullName, "Engine", "Source");
                if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "Runtime")))
                    return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        private string FindEngineFromUProject()
        {
            try
            {
                var json = JObject.Parse(File.ReadAllText(UProjectFilePath));
                var association = json.Value<string>("EngineAssociation");
                if (string.IsNullOrEmpty(association))
                    return null;

                EngineVersion = association;

                // Try registry (installed builds)
                var regPaths = new[]
                {
                    $@"SOFTWARE\EpicGames\Unreal Engine\{association}",
                    $@"SOFTWARE\WOW6432Node\EpicGames\Unreal Engine\{association}"
                };

                foreach (var regPath in regPaths)
                {
                    using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regPath))
                    {
                        var installDir = key?.GetValue("InstalledDirectory") as string;
                        if (!string.IsNullOrEmpty(installDir))
                        {
                            var src = Path.Combine(installDir, "Engine", "Source");
                            if (Directory.Exists(src))
                                return src;
                        }
                    }
                }

                // Also check HKCU for source builds
                using (var builds = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds"))
                {
                    if (builds != null)
                    {
                        var buildDir = builds.GetValue(association) as string;
                        if (!string.IsNullOrEmpty(buildDir))
                        {
                            var src = Path.Combine(buildDir, "Engine", "Source");
                            if (Directory.Exists(src))
                                return src;
                        }
                    }
                }
            }
            catch
            {
                // Swallow JSON/registry errors — best effort
            }

            return null;
        }

        // ── Helpers ─────────────────────────────────────────────────

        private static string FindUProjectFile(string directory)
        {
            try
            {
                var files = Directory.GetFiles(directory, "*.uproject", SearchOption.TopDirectoryOnly);
                return files.FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string> GetSolutionDirectoryAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var solution = await package.GetServiceAsync(typeof(SVsSolution)).ConfigureAwait(true) as IVsSolution;
            if (solution == null) return null;

            solution.GetSolutionInfo(out string solutionDir, out _, out _);
            return solutionDir;
        }
    }
}
