using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
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
        public async Task DetectAsync(AsyncPackage package, string engineRootOverride)
        {
            await Task.Yield(); // ensure we're off the UI thread

            SolutionDirectory = await GetSolutionDirectoryAsync(package).ConfigureAwait(false);
            if (string.IsNullOrEmpty(SolutionDirectory))
                return;

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
                return;

            IsUnrealProject = true;
            ProjectRoot = Path.GetDirectoryName(UProjectFilePath);
            ProjectSourceRoot = Path.Combine(ProjectRoot, "Source");

            // Step 2: Find engine source
            EngineSourceRoot = ResolveEngineSource(engineRootOverride);
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
