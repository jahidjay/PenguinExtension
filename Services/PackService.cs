using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PenguinExtention.Completion;
using PenguinExtention.Models;

namespace PenguinExtention.Services
{
    internal enum PackLocation { BuiltIn, User, Solution }

    /// <summary>A folder to index and the pack it belongs to ("external" = a library no pack claims).</summary>
    internal struct PackRoot
    {
        public string Path;
        public string PackId;
        public PackRoot(string path, string packId) { Path = path; PackId = packId; }
    }

    /// <summary>A method the engine calls by name (Unity's Update, OnTriggerEnter, …), from a pack's callbacks.json.</summary>
    internal sealed class PackCallback
    {
        public string Name, Owner, Signature, Description, DocPage, Access, Language;
        public PackInfo Pack;
        /// <summary>Method stub inserted on completion, with $0 where the caret goes.</summary>
        public string Stub => (string.IsNullOrEmpty(Access) ? "private" : Access) + " " + Signature + "\n{\n    $0\n}";
    }

    /// <summary>One discovered pack and its state for the open solution.</summary>
    internal sealed class PackInfo
    {
        public PackManifest Manifest;
        public string Directory;
        public PackLocation Location;
        public bool Detected;
        /// <summary>The solution's check mark: null = follow detection.</summary>
        public bool? Choice;
        public bool Enabled => Error == null && (Choice ?? Detected);
        /// <summary>Project include directories this pack claimed via detect.includeHeaders.</summary>
        public List<string> ClaimedRoots = new List<string>();
        public string Error;
        public string Id => Manifest?.Id ?? System.IO.Path.GetFileName(Directory);
    }

    /// <summary>
    /// Finds packs (built-in, user-global, per-solution), detects which apply to the open solution, applies the
    /// solution's check marks, and serves the enabled packs' specifiers, snippets and index roots.
    /// State is an immutable snapshot swapped in one assignment, so readers on any thread never lock.
    /// </summary>
    internal static class PackService
    {
        public const string EnginePackId = "unreal";
        public const string ExternalPackId = "external";
        private static readonly Regex IdPattern = new Regex(@"^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

        private sealed class Snapshot
        {
            public static readonly Snapshot Empty = new Snapshot();
            public IReadOnlyList<PackInfo> Packs = new PackInfo[0];
            public Dictionary<string, UnrealMacroSpecifiers.Specifier[]> Macros = new Dictionary<string, UnrealMacroSpecifiers.Specifier[]>();
            public HashSet<string> MacroNames = new HashSet<string>();
            public HashSet<string> SpecifierNames = new HashSet<string>(StringComparer.Ordinal); // every macro's keys, flat
            public UnrealSnippets.Snippet[] Snippets = new UnrealSnippets.Snippet[0];      // C/C++ only
            public UnrealSnippets.Snippet[] AllSnippets = new UnrealSnippets.Snippet[0];   // every language
            public PackCallback[] Callbacks = new PackCallback[0];
            public Dictionary<string, string> DefaultOwners = new Dictionary<string, string>(); // language → owner for unknown base classes
            public HashSet<string> Languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public string SolutionDirectory, DocsVersion, EditorPath;
        }

        private static volatile Snapshot _current = Snapshot.Empty;

        /// <summary>Raised after a check mark changes or packs were imported/removed (the package restarts and reloads).</summary>
        public static event Action Changed;
        /// <summary>Raised after every <see cref="Load"/> (the Packs window refreshes).</summary>
        public static event Action Loaded;

        /// <summary>Re-reads every pack folder (after an import or a manual edit).</summary>
        public static void Reload() => Changed?.Invoke();

        public static IReadOnlyList<PackInfo> Packs => _current.Packs;
        public static bool IsLoaded => _current != Snapshot.Empty;
        public static UnrealMacroSpecifiers.Specifier[] GetSpecifiers(string macro) =>
            macro != null && _current.Macros.TryGetValue(macro, out var list) ? list : null;
        /// <summary>Macros with specifier lists, excluding "meta" (which lives inside another macro).</summary>
        public static HashSet<string> MacroNames => _current.MacroNames;
        /// <summary>True when the word is a specifier or meta key of any reflection macro in the enabled packs.</summary>
        public static bool IsSpecifierName(string word) => word != null && _current.SpecifierNames.Contains(word);
        public static UnrealSnippets.Snippet[] Snippets => _current.Snippets;
        /// <summary>Snippets of enabled packs for one language ("csharp", "gdscript", "cpp").</summary>
        public static UnrealSnippets.Snippet[] SnippetsFor(string language) =>
            _current.AllSnippets.Where(s => string.Equals(s.Language, language, StringComparison.OrdinalIgnoreCase)).ToArray();
        public static IReadOnlyList<PackCallback> Callbacks => _current.Callbacks;
        /// <summary>Owner whose callbacks apply when a class's base type is not a known owner (Unity: MonoBehaviour).</summary>
        public static string DefaultOwner(string language) => _current.DefaultOwners.TryGetValue(language ?? "", out var o) ? o : null;
        /// <summary>True when an enabled pack serves this language.</summary>
        public static bool HasLanguage(string language) => _current.Languages.Contains(language);
        public static string SolutionDirectory => _current.SolutionDirectory;
        /// <summary>Enabled packs' suggested MCP servers.</summary>
        public static IEnumerable<McpServerConfig> McpSuggestions =>
            _current.Packs.Where(p => p.Enabled && p.Manifest?.Mcp != null)
                .SelectMany(p => p.Manifest.Mcp.Where(m => m != null && !string.IsNullOrEmpty(m.Name)).Select(m => { m.Source = "pack " + p.Id; return m; }));

        /// <summary>Docs for a callback: the installed offline page when present, else the online page for the project's engine version.</summary>
        public static string DocsUrl(PackCallback cb)
        {
            var m = cb?.Pack?.Manifest;
            if (m == null || string.IsNullOrEmpty(cb.DocPage)) return null;
            var snap = _current;
            if (!string.IsNullOrEmpty(m.LocalDocs) && !string.IsNullOrEmpty(snap.EditorPath))
            {
                try
                {
                    var local = Path.GetFullPath(m.LocalDocs.Replace("{editor}", snap.EditorPath).Replace("{page}", cb.DocPage));
                    if (File.Exists(local)) return local;
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { }
            }
            if (string.IsNullOrEmpty(m.DocsUrl)) return null;
            var url = string.IsNullOrEmpty(snap.DocsVersion) ? m.DocsUrl.Replace("{version}/", "") : m.DocsUrl.Replace("{version}", snap.DocsVersion);
            return url.Replace("{page}", cb.DocPage);
        }
        public static bool IsEnabled(string id) => _current.Packs.Any(p => p.Id == id && p.Enabled);

        public static string BuiltInDirectory =>
            Path.Combine(Path.GetDirectoryName(typeof(PackService).Assembly.Location), "Packs");
        public static string UserDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PenguinExtension", "Packs");
        public static string SolutionPacksDirectory(string solutionDir) => Path.Combine(solutionDir, ".penguin", "packs");
        private static string StatePath(string solutionDir) => Path.Combine(solutionDir, ".vs", "PenguinExtension", "packs.json");

        // ── Loading (background thread) ─────────────────────────────

        /// <summary>Discovers and detects packs for the detected project. File I/O: call off the UI thread.</summary>
        public static void Load(UnrealProjectDetector detector)
        {
            var solutionDir = detector?.SolutionDirectory;
            var byId = new Dictionary<string, PackInfo>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<PackInfo>();
            void Scan(string dir, PackLocation location)
            {
                if (string.IsNullOrEmpty(dir) || !System.IO.Directory.Exists(dir)) return;
                foreach (var packDir in SafeDirectories(dir))
                {
                    var info = ReadPack(packDir, location);
                    if (info == null) continue;
                    if (info.Error != null) { errors.Add(info); continue; }
                    if (byId.TryGetValue(info.Id, out var old))
                        PenguinLog.Info("Packs", info.Id + ": " + info.Location + " copy overrides " + old.Location + " (" + old.Directory + ")");
                    byId[info.Id] = info; // later locations win
                }
            }
            Scan(BuiltInDirectory, PackLocation.BuiltIn);
            Scan(UserDirectory, PackLocation.User);
            if (solutionDir != null) Scan(SolutionPacksDirectory(solutionDir), PackLocation.Solution);

            var choices = solutionDir == null ? new Dictionary<string, bool>() : ReadState(solutionDir);
            foreach (var p in byId.Values)
            {
                Detect(p, detector);
                if (choices.TryGetValue(p.Id, out var on)) p.Choice = on;
            }

            var packs = byId.Values.OrderBy(p => p.Location).ThenBy(p => p.Id).Concat(errors).ToList();
            var snap = new Snapshot
            {
                Packs = packs, SolutionDirectory = solutionDir,
                DocsVersion = detector?.UnityDocsVersion ?? detector?.EngineVersion,
                EditorPath = detector?.UnityEditorPath
            };
            var snippets = new List<UnrealSnippets.Snippet>();
            var callbacks = new List<PackCallback>();
            foreach (var p in packs.Where(x => x.Enabled))
            {
                try
                {
                    LoadSpecifiers(p, snap.Macros);
                    snippets.AddRange(LoadSnippets(p));
                    callbacks.AddRange(LoadCallbacks(p, snap.DefaultOwners));
                    foreach (var lang in PackLanguages(p)) snap.Languages.Add(lang);
                }
                catch (Exception ex) { p.Error = "data file: " + ex.Message; PenguinLog.Error("Packs", p.Id + ": " + ex.Message); }
            }
            snap.AllSnippets = snippets.ToArray();
            snap.Snippets = snippets.Where(x => x.Language == "cpp").ToArray();
            snap.Callbacks = callbacks.ToArray();
            snap.MacroNames = new HashSet<string>(snap.Macros.Keys.Where(k => k != "meta"));
            snap.SpecifierNames = new HashSet<string>(snap.Macros.Values.SelectMany(v => v).Select(x => x.Name), StringComparer.Ordinal);
            _current = snap;
            PenguinLog.Info("Packs", packs.Count == 0 ? "no packs found (looked in " + BuiltInDirectory + ")"
                : string.Join(", ", packs.Select(p => p.Id + (p.Error != null ? " [error]" : p.Enabled ? " [on]" : " [off]"))));
            try { Loaded?.Invoke(); } catch (Exception ex) { PenguinLog.Error("Packs", ex); }
        }

        private static string[] PackLanguages(PackInfo p) =>
            p.Manifest.Languages != null && p.Manifest.Languages.Length > 0 ? p.Manifest.Languages.Select(l => l.ToLowerInvariant()).ToArray() : new[] { "cpp" };

        private static PackInfo ReadPack(string packDir, PackLocation location)
        {
            var file = Path.Combine(packDir, "pack.json");
            if (!File.Exists(file)) return null;
            var info = new PackInfo { Directory = packDir, Location = location };
            try
            {
                info.Manifest = JsonConvert.DeserializeObject<PackManifest>(File.ReadAllText(file)) ?? new PackManifest();
                if (string.IsNullOrEmpty(info.Manifest.Id) || !IdPattern.IsMatch(info.Manifest.Id))
                    info.Error = "\"id\" missing or invalid (lowercase letters, digits, dashes)";
                info.Manifest.Detect = info.Manifest.Detect ?? new PackDetect();
            }
            catch (Exception ex) { info.Error = "pack.json: " + ex.Message; }
            if (info.Error != null) PenguinLog.Error("Packs", file + ": " + info.Error);
            return info;
        }

        private static void Detect(PackInfo p, UnrealProjectDetector detector)
        {
            if (detector?.SolutionDirectory == null) return;
            var d = p.Manifest.Detect;
            foreach (var header in d.IncludeHeaders ?? new string[0])
                foreach (var root in detector.ExternalIncludeRoots)
                    if (File.Exists(Path.Combine(root, header)) && !p.ClaimedRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
                        p.ClaimedRoots.Add(root);
            p.Detected = p.ClaimedRoots.Count > 0 || (d.Files ?? new string[0]).Any(f => FileNearSolution(detector.SolutionDirectory, f));
        }

        /// <summary>True when <paramref name="pattern"/> exists in the solution directory or one of its 4 parents.</summary>
        private static bool FileNearSolution(string solutionDir, string pattern)
        {
            var dir = new DirectoryInfo(solutionDir);
            for (int i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
            {
                try
                {
                    if (pattern.IndexOfAny(new[] { '*', '?' }) >= 0
                        ? System.IO.Directory.EnumerateFiles(dir.FullName, pattern, SearchOption.TopDirectoryOnly).Any()
                        : File.Exists(Path.Combine(dir.FullName, pattern)) || System.IO.Directory.Exists(Path.Combine(dir.FullName, pattern)))
                        return true;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) { }
            }
            return false;
        }

        private static void LoadSpecifiers(PackInfo p, Dictionary<string, UnrealMacroSpecifiers.Specifier[]> into)
        {
            if (string.IsNullOrEmpty(p.Manifest.Specifiers)) return;
            var macros = JObject.Parse(File.ReadAllText(Path.Combine(p.Directory, p.Manifest.Specifiers)))["macros"] as JObject;
            if (macros == null) return;
            foreach (var prop in macros.Properties())
            {
                var list = prop.Value.Select(t => new UnrealMacroSpecifiers.Specifier(
                    (string)t["name"], (string)t["description"] ?? "", (string)t["insert"])).Where(s => !string.IsNullOrEmpty(s.Name));
                into[prop.Name] = into.TryGetValue(prop.Name, out var existing) ? existing.Concat(list).ToArray() : list.ToArray();
            }
        }

        private static IEnumerable<UnrealSnippets.Snippet> LoadSnippets(PackInfo p)
        {
            if (string.IsNullOrEmpty(p.Manifest.Snippets)) return Enumerable.Empty<UnrealSnippets.Snippet>();
            var items = JObject.Parse(File.ReadAllText(Path.Combine(p.Directory, p.Manifest.Snippets)))["snippets"] as JArray;
            if (items == null) return Enumerable.Empty<UnrealSnippets.Snippet>();
            var language = PackLanguages(p)[0];
            return items.Select(t => new UnrealSnippets.Snippet((string)t["keyword"], (string)t["text"],
                    (string)t["description"] ?? "", (int?)t["caretBack"] ?? 0)
                    { Language = ((string)t["language"] ?? language).ToLowerInvariant(), PackId = p.Id })
                .Where(s => !string.IsNullOrEmpty(s.Keyword) && !string.IsNullOrEmpty(s.Text)).ToList();
        }

        private static IEnumerable<PackCallback> LoadCallbacks(PackInfo p, Dictionary<string, string> defaultOwners)
        {
            if (string.IsNullOrEmpty(p.Manifest.Callbacks)) return Enumerable.Empty<PackCallback>();
            var root = JObject.Parse(File.ReadAllText(Path.Combine(p.Directory, p.Manifest.Callbacks)));
            var language = ((string)root["language"] ?? PackLanguages(p)[0]).ToLowerInvariant();
            var defaultOwner = (string)root["defaultOwner"];
            if (!string.IsNullOrEmpty(defaultOwner) && !defaultOwners.ContainsKey(language)) defaultOwners[language] = defaultOwner;
            if (!(root["callbacks"] is JArray items)) return Enumerable.Empty<PackCallback>();
            return items.Select(t => new PackCallback
            {
                Name = (string)t["name"], Owner = (string)t["owner"] ?? defaultOwner, Signature = (string)t["signature"],
                Description = (string)t["description"] ?? "", DocPage = (string)t["docPage"], Access = (string)t["access"],
                Language = language, Pack = p
            }).Where(c => !string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(c.Signature)).ToList();
        }

        // ── Index roots ─────────────────────────────────────────────

        /// <summary>
        /// Library folders to index: each enabled pack's own includeRoots, plus the project's library include
        /// directories (tagged with the pack that claimed them, or "external"). Roots claimed by a disabled pack are left out.
        /// </summary>
        public static List<PackRoot> IndexRoots(IEnumerable<string> projectIncludeRoots)
        {
            var packs = _current.Packs;
            var roots = new List<PackRoot>();
            foreach (var p in packs.Where(x => x.Enabled))
                foreach (var rel in p.Manifest.IncludeRoots ?? new string[0])
                {
                    string full = null;
                    try { full = Path.GetFullPath(Path.Combine(p.Directory, rel)); }
                    catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { }
                    if (full != null && System.IO.Directory.Exists(full)) roots.Add(new PackRoot(full, p.Id));
                    else PenguinLog.Error("Packs", p.Id + ": includeRoot not found: " + (full ?? rel));
                }
            foreach (var root in projectIncludeRoots ?? Enumerable.Empty<string>())
            {
                var owner = packs.FirstOrDefault(p => p.ClaimedRoots.Contains(root, StringComparer.OrdinalIgnoreCase));
                if (owner == null) roots.Add(new PackRoot(root, ExternalPackId));
                else if (owner.Enabled) roots.Add(new PackRoot(root, owner.Id));
            }
            return roots;
        }

        /// <summary>Ids of packs that are present but unchecked: their indexed symbols are removed.</summary>
        public static HashSet<string> DisabledPackIds() =>
            new HashSet<string>(_current.Packs.Where(p => !p.Enabled && p.Manifest?.Id != null && IdPattern.IsMatch(p.Manifest.Id)).Select(p => p.Id));

        // ── Check marks ─────────────────────────────────────────────

        /// <summary>Sets (true/false) or clears (null = follow detection) a pack's check mark for the open solution.</summary>
        public static Task SetEnabledAsync(string packId, bool? enabled) => Task.Run(() =>
        {
            var solutionDir = _current.SolutionDirectory;
            if (solutionDir == null) throw new InvalidOperationException("No solution is open.");
            var state = ReadState(solutionDir);
            if (enabled == null) state.Remove(packId); else state[packId] = enabled.Value;
            var path = StatePath(solutionDir);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonConvert.SerializeObject(new { enabled = state }, Formatting.Indented));
            PenguinLog.Info("Packs", packId + " → " + (enabled == null ? "auto" : enabled.Value ? "on" : "off"));
            Changed?.Invoke();
        });

        private static Dictionary<string, bool> ReadState(string solutionDir)
        {
            var path = StatePath(solutionDir);
            var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(path)) return result;
            try
            {
                if (JObject.Parse(File.ReadAllText(path))["enabled"] is JObject enabled)
                    foreach (var prop in enabled.Properties())
                        if (prop.Value.Type == JTokenType.Boolean) result[prop.Name] = (bool)prop.Value;
            }
            catch (Exception ex) { PenguinLog.Error("Packs", path + ": " + ex.Message + " (check marks ignored)"); }
            return result;
        }

        private static IEnumerable<string> SafeDirectories(string dir)
        {
            try { return System.IO.Directory.GetDirectories(dir); }
            catch (Exception ex) { PenguinLog.Error("Packs", dir + ": " + ex.Message); return new string[0]; }
        }
    }
}
