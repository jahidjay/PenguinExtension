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
        public async Task DetectAsync(AsyncPackage package, string engineRootOverride, string extraIncludeDirectories = null, string unityEditorOverride = null)
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
                if (DetectUnity(unityEditorOverride)) return; // Unity: C# only, no C++ indexing of Library/PackageCache
                DetectPlainCpp(extraIncludeDirectories);
                return;
            }

            IsUnrealProject = true;
            ProjectRoot = Path.GetDirectoryName(UProjectFilePath);
            ProjectSourceRoot = Path.Combine(ProjectRoot, "Source");

            // Step 2: Find engine source
            EngineSourceRoot = ResolveEngineSource(engineRootOverride);
        }

        // ── Unity projects (any editor version) ────────────────────

        /// <summary>True when the solution belongs to a Unity project (Assets/ + ProjectSettings/ProjectVersion.txt).</summary>
        public bool IsUnityProject { get; private set; }
        /// <summary>Editor version from ProjectVersion.txt, e.g. "2022.3.10f1" or "6000.0.23f1".</summary>
        public string UnityVersion { get; private set; }
        /// <summary>Folder holding Unity.exe for that version, or null when it is not installed.</summary>
        public string UnityEditorPath { get; private set; }
        /// <summary>True when <see cref="UnityEditorPath"/> is exactly the project's version (not a fallback install).</summary>
        public bool UnityEditorExact { get; private set; }
        /// <summary>docs.unity3d.com version segment ("2022.3", "6000.0").</summary>
        public string UnityDocsVersion { get; private set; }

        private bool DetectUnity(string editorOverride)
        {
            var root = UnityEditorLocator.FindProjectRoot(SolutionDirectory);
            if (root == null) return false;
            IsUnityProject = true;
            ProjectRoot = root;
            ProjectSourceRoot = Path.Combine(root, "Assets");
            UnityVersion = UnityEditorLocator.ReadVersion(root);
            EngineVersion = UnityVersion;
            UnityDocsVersion = UnityEditorLocator.DocsVersion(UnityVersion);
            UnityEditorPath = UnityEditorLocator.FindEditor(root, UnityVersion, editorOverride, out var exact);
            UnityEditorExact = exact;
            return true;
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
                if (SafeEnumerate(dir.FullName, "*.sln", 1).Any() || SafeEnumerate(dir.FullName, "*.slnx", 1).Any())
                    return dir.FullName.TrimEnd('\\') + "\\";
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

                // Epic Games Launcher manifest (%ProgramData%\Epic\UnrealEngineLauncher\LauncherInstalled.dat) —
                // newer UE 5.x installs (e.g. 5.8, 5.9) are often listed here without an HKLM registry subkey.
                var fromLauncher = FindEngineFromLauncherManifest(association);
                if (fromLauncher != null)
                    return fromLauncher;

                // Standard & sibling install roots (e.g. C:\Program Files\Epic Games\UE_5.9, D:\Unreal Engine\UE_5.8)
                var fromDisk = FindEngineFromKnownRoots(association);
                if (fromDisk != null)
                    return fromDisk;
            }
            catch
            {
                // Swallow JSON/registry errors — best effort
            }

            // Fallback: extract engine path referenced inside generated solution / UE5.vcxproj files
            return FindEngineFromProjectFiles();
        }

        private static string FindEngineFromLauncherManifest(string association)
        {
            try
            {
                var datPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
                if (!File.Exists(datPath)) return null;

                var root = JObject.Parse(File.ReadAllText(datPath));
                var list = root["InstallationList"] as JArray;
                if (list == null) return null;

                var targetId = "UE_" + association;
                foreach (var item in list.OfType<JObject>())
                {
                    var artifact = item.Value<string>("ArtifactId");
                    var appName = item.Value<string>("AppName");
                    if (!string.Equals(artifact, targetId, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(appName, targetId, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(artifact, association, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var installDir = item.Value<string>("InstallLocation");
                    if (!string.IsNullOrEmpty(installDir))
                    {
                        var src = Path.Combine(installDir, "Engine", "Source");
                        if (Directory.Exists(src)) return src;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string FindEngineFromKnownRoots(string association)
        {
            var folderName = "UE_" + association;
            var parentDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Epic Games")
            };

            foreach (var regRoot in new[] { @"SOFTWARE\EpicGames\Unreal Engine", @"SOFTWARE\WOW6432Node\EpicGames\Unreal Engine" })
            {
                try
                {
                    using (var key = Registry.LocalMachine.OpenSubKey(regRoot))
                    {
                        if (key == null) continue;
                        foreach (var subName in key.GetSubKeyNames())
                        {
                            using (var sub = key.OpenSubKey(subName))
                            {
                                var dir = (sub?.GetValue("InstalledDirectory") as string)?.TrimEnd('\\', '/');
                                var parent = string.IsNullOrEmpty(dir) ? null : Path.GetDirectoryName(dir);
                                if (!string.IsNullOrEmpty(parent)) parentDirs.Add(parent);
                            }
                        }
                    }
                }
                catch { }
            }

            foreach (var parent in parentDirs)
            {
                try
                {
                    var src = Path.Combine(parent, folderName, "Engine", "Source");
                    if (Directory.Exists(src)) return src;
                }
                catch { }
            }
            return null;
        }

        private string FindEngineFromProjectFiles()
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(ProjectRoot))
            {
                var ueProj = Path.Combine(ProjectRoot, "Intermediate", "ProjectFiles", "UE5.vcxproj");
                if (File.Exists(ueProj)) candidates.Add(ueProj);
                candidates.AddRange(SafeEnumerate(ProjectRoot, "*.sln", 1));
                candidates.AddRange(SafeEnumerate(ProjectRoot, "*.slnx", 1));
            }
            if (!string.IsNullOrEmpty(SolutionDirectory) && !string.Equals(SolutionDirectory, ProjectRoot, StringComparison.OrdinalIgnoreCase))
            {
                candidates.AddRange(SafeEnumerate(SolutionDirectory, "*.sln", 1));
                candidates.AddRange(SafeEnumerate(SolutionDirectory, "*.slnx", 1));
            }

            var rx = new System.Text.RegularExpressions.Regex(
                @"([A-Za-z]:\\[^""<>|\r\n]*?)\\Engine\\(?:Source|Build|Intermediate|Extras)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (var file in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (System.Text.RegularExpressions.Match m in rx.Matches(File.ReadAllText(file)))
                    {
                        var src = Path.Combine(m.Groups[1].Value, "Engine", "Source");
                        if (Directory.Exists(src)) return src;
                    }
                }
                catch { }
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
