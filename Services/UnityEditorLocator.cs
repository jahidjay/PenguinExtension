using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Unity project facts that work for every editor version: the version from ProjectSettings/ProjectVersion.txt,
    /// the matching editor install (Unity Hub, a custom Hub folder, the generated .csproj, old standalone installers)
    /// and the documentation version used in docs.unity3d.com links. Pure file I/O: call off the UI thread.
    /// </summary>
    internal static class UnityEditorLocator
    {
        private static readonly Regex VersionLine = new Regex(@"^\s*m_EditorVersion:\s*(\S+)", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex HintPath = new Regex(@"<HintPath>([^<]*?)[\\/]Data[\\/]Managed[\\/]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ExePath = new Regex(@"""((?:[^""\\]|\\.)*?Unity\.exe)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MajorMinor = new Regex(@"^(\d+)\.(\d+)", RegexOptions.Compiled);

        /// <summary>The Unity project folder (has Assets/ and ProjectSettings/ProjectVersion.txt) at or above <paramref name="startDir"/>.</summary>
        public static string FindProjectRoot(string startDir)
        {
            if (string.IsNullOrEmpty(startDir)) return null;
            try
            {
                var dir = new DirectoryInfo(startDir);
                for (int i = 0; i < 3 && dir != null; i++, dir = dir.Parent)
                    if (File.Exists(VersionFile(dir.FullName)) && Directory.Exists(Path.Combine(dir.FullName, "Assets")))
                        return dir.FullName;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { }
            return null;
        }

        public static string VersionFile(string projectRoot) => Path.Combine(projectRoot, "ProjectSettings", "ProjectVersion.txt");

        /// <summary>"2022.3.10f1", "6000.0.23f1", "2019.4.40f1", … or null.</summary>
        public static string ReadVersion(string projectRoot)
        {
            try
            {
                var m = VersionLine.Match(File.ReadAllText(VersionFile(projectRoot)));
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        /// <summary>Version segment of docs.unity3d.com URLs: "2022.3", "6000.0", "560" for 5.6; null when unknown.</summary>
        public static string DocsVersion(string version)
        {
            var m = MajorMinor.Match(version ?? "");
            if (!m.Success) return null;
            int major = int.Parse(m.Groups[1].Value), minor = int.Parse(m.Groups[2].Value);
            if (major >= 2017) return major + "." + minor;
            if (major == 5) return "5" + minor + "0";
            return null;
        }

        /// <summary>
        /// The editor folder (the one holding Unity.exe) for <paramref name="version"/>. Exact version matches win;
        /// the .csproj hint path and older installs are fallbacks. <paramref name="exact"/> tells which one was found.
        /// </summary>
        public static string FindEditor(string projectRoot, string version, string overridePath, out bool exact)
        {
            exact = false;
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                var o = overridePath.Trim().Trim('"');
                if (o.EndsWith("Unity.exe", StringComparison.OrdinalIgnoreCase)) o = Path.GetDirectoryName(o);
                if (IsEditor(o)) { exact = true; return o; }
                if (IsEditor(Path.Combine(o, "Editor"))) { exact = true; return Path.Combine(o, "Editor"); }
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var hubData = Path.Combine(appData, "UnityHub");
            if (!string.IsNullOrEmpty(version))
            {
                var exactCandidates = new List<string>();
                foreach (var pf in ProgramFilesFolders())
                    exactCandidates.Add(Path.Combine(pf, "Unity", "Hub", "Editor", version, "Editor"));
                var secondary = ReadJsonString(Path.Combine(hubData, "secondaryInstallPath.json"));
                if (!string.IsNullOrWhiteSpace(secondary)) exactCandidates.Add(Path.Combine(secondary, version, "Editor"));
                exactCandidates.AddRange(HubListedEditors(hubData).Where(e => e.IndexOf(version, StringComparison.OrdinalIgnoreCase) >= 0));
                var hit = exactCandidates.FirstOrDefault(IsEditor);
                if (hit != null) { exact = true; return hit; }
            }

            // Fallbacks: the editor that generated the project's .csproj, then old single-version installs.
            var fromCsproj = EditorFromCsproj(projectRoot);
            if (fromCsproj != null)
            {
                exact = !string.IsNullOrEmpty(version) && fromCsproj.IndexOf(version, StringComparison.OrdinalIgnoreCase) >= 0;
                return fromCsproj;
            }
            var legacy = new List<string>();
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Unity Technologies\Installer\Unity"))
                {
                    var loc = (key?.GetValue("Location x64") ?? key?.GetValue("Location")) as string;
                    if (!string.IsNullOrEmpty(loc)) legacy.Add(Path.Combine(loc, "Editor"));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException || ex is UnauthorizedAccessException || ex is IOException) { }
            foreach (var pf in ProgramFilesFolders()) legacy.Add(Path.Combine(pf, "Unity", "Editor"));
            return legacy.FirstOrDefault(IsEditor);
        }

        /// <summary>Editor folder taken from a UnityEngine reference HintPath in the project's generated .csproj files.</summary>
        internal static string EditorFromCsproj(string projectRoot)
        {
            if (string.IsNullOrEmpty(projectRoot)) return null;
            string[] projects;
            try { projects = Directory.GetFiles(projectRoot, "*.csproj", SearchOption.TopDirectoryOnly); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
            foreach (var proj in projects.OrderBy(p => Path.GetFileName(p).StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                string xml;
                try { xml = File.ReadAllText(proj); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                foreach (Match m in HintPath.Matches(xml))
                {
                    var dir = m.Groups[1].Value.Replace('/', '\\');
                    if (IsEditor(dir)) return dir;
                }
            }
            return null;
        }

        /// <summary>Unity.exe paths listed in Unity Hub's editors.json / editors-v2.json (manually located editors).</summary>
        private static IEnumerable<string> HubListedEditors(string hubData)
        {
            foreach (var name in new[] { "editors-v2.json", "editors.json" })
            {
                string text;
                try { text = File.ReadAllText(Path.Combine(hubData, name)); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                foreach (Match m in ExePath.Matches(text))
                {
                    string dir = null;
                    try { dir = Path.GetDirectoryName(Regex.Unescape(m.Groups[1].Value)); } catch (ArgumentException) { }
                    if (dir != null) yield return dir;
                }
            }
        }

        private static string ReadJsonString(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var token = Newtonsoft.Json.Linq.JToken.Parse(File.ReadAllText(path));
                return token.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)token : null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException) { return null; }
        }

        private static IEnumerable<string> ProgramFilesFolders() =>
            new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) }
                .Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase);

        private static bool IsEditor(string dir)
        {
            try { return !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, "Unity.exe")); }
            catch (ArgumentException) { return false; }
        }
    }
}
