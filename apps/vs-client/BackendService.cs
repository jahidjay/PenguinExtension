using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PenguinExtention.Models;
using PenguinExtention.Services;

namespace PenguinExtention.Core
{
    internal interface ISymbolBackend
    {
        Task<List<UnrealSymbol>> SearchAsync(string query, int limit, CancellationToken token);
        Task<UnrealClassInfo> ClassInfoAsync(UnrealSymbol symbol, CancellationToken token);
        /// <summary>Raw name-based occurrences of <paramref name="symbol"/>'s name (certainty not yet assigned).</summary>
        Task<List<SymbolOccurrence>> OccurrencesAsync(UnrealSymbol symbol, CancellationToken token);
        /// <summary>Direct base names and derived class symbols.</summary>
        Task<InheritanceInfo> InheritanceAsync(UnrealSymbol symbol, CancellationToken token);
    }
    internal sealed class InheritanceInfo
    {
        public List<string> Bases { get; set; } = new List<string>();
        public List<UnrealSymbol> Derived { get; set; } = new List<UnrealSymbol>();
    }
    internal sealed class LegacyBackend : ISymbolBackend
    {
        private readonly CacheService cache;
        public LegacyBackend(CacheService cache) { this.cache = cache; }
        public Task<List<UnrealSymbol>> SearchAsync(string query, int limit, CancellationToken token) => Task.Run(() =>
            string.IsNullOrWhiteSpace(query) ? cache.GetAllSymbols(limit: limit) : cache.Search(query, null, limit), token);
        public Task<UnrealClassInfo> ClassInfoAsync(UnrealSymbol symbol, CancellationToken token) => Task.Run(() => cache.GetClassInfo(symbol.Name), token);
        public Task<List<SymbolOccurrence>> OccurrencesAsync(UnrealSymbol symbol, CancellationToken token) => cache.GetOccurrencesAsync(symbol.Name);
        public Task<InheritanceInfo> InheritanceAsync(UnrealSymbol symbol, CancellationToken token) => Task.Run(() =>
        {
            var info = new InheritanceInfo { Derived = cache.GetDerivedClasses(symbol.Name) };
            var baseClass = cache.GetClassInfo(symbol.Name)?.BaseClass;
            if (!string.IsNullOrEmpty(baseClass)) info.Bases.Add(baseClass);
            return info;
        }, token);
    }
    internal sealed class CoreBackend : ISymbolBackend
    {
        private readonly CoreClientService client;
        private readonly string engineRoot;
        public CoreBackend(CoreClientService client, string engineRoot) { this.client = client; this.engineRoot = engineRoot; }
        public async Task<List<UnrealSymbol>> SearchAsync(string query, int limit, CancellationToken token)
        {
            var result = await client.RequestAsync("penguin/symbols", new { query = query ?? "", limit }, token).ConfigureAwait(false);
            return (result as JArray ?? new JArray()).Select(s => Protocol.Symbol(s, engineRoot)).Where(s => s != null).ToList();
        }
        public Task<UnrealClassInfo> ClassInfoAsync(UnrealSymbol symbol, CancellationToken token) => Task.FromResult(new UnrealClassInfo
        {
            ClassName = symbol.Name, BaseClass = symbol.Bases.FirstOrDefault(), MetaSpecifiers = symbol.Specifiers,
            // Bases are direct bases, not a fabricated transitive inheritance chain.
            InheritanceChain = new List<string>()
        });
        public async Task<List<SymbolOccurrence>> OccurrencesAsync(UnrealSymbol symbol, CancellationToken token)
        {
            // Opt-in protocol extension; older Core binaries simply don't advertise it.
            if ((bool?)client.Capabilities["experimental"]?["penguin"]?["references"] != true)
            {
                // Older Core: scan project sources client-side with the same comment/string-aware scanner.
                var files = await SolutionFileIndex.GetAsync(token).ConfigureAwait(false);
                var found = new List<SymbolOccurrence>();
                foreach (var file in files.Where(f => HeaderSourceResolver.IsHeader(f) || HeaderSourceResolver.IsSource(f)))
                {
                    token.ThrowIfCancellationRequested();
                    string text;
                    try { text = System.IO.File.ReadAllText(file); } catch { continue; }
                    if (text.IndexOf(symbol.Name, StringComparison.Ordinal) < 0) continue;
                    foreach (var o in OccurrenceScanner.ScanFor(text, symbol.Name)) { o.FilePath = file; found.Add(o); }
                }
                return found;
            }
            var result = await client.RequestAsync("penguin/references", new { name = symbol.Name }, token).ConfigureAwait(false);
            return (result as JArray ?? new JArray()).Select(o => new SymbolOccurrence
            {
                SymbolName = symbol.Name, FilePath = (string)o["file"],
                LineNumber = Math.Max(1, (int?)o["line"] ?? 1), ColumnNumber = Math.Max(0, (int?)o["column"] ?? 0),
                Context = (OccurrenceContext)((int?)o["context"] ?? 0), Qualifier = (string)o["qualifier"]
            }).Where(o => !string.IsNullOrEmpty(o.FilePath)).ToList();
        }
        public async Task<InheritanceInfo> InheritanceAsync(UnrealSymbol symbol, CancellationToken token)
        {
            var result = await client.RequestAsync("penguin/inheritance", new { name = symbol.Name }, token).ConfigureAwait(false);
            return new InheritanceInfo
            {
                Bases = (result?["bases"] as JArray)?.Values<string>().ToList() ?? new List<string>(),
                Derived = (result?["derived"] as JArray ?? new JArray()).Select(s => Protocol.Symbol(s, engineRoot)).Where(s => s != null).ToList()
            };
        }
    }
    internal static class BackendService
    {
        private static ISymbolBackend backend;
        public static CoreClientService Core { get; private set; }
        public static PenguinBackend Mode { get; private set; } = PenguinBackend.Legacy;
        public static bool CompletionEnabled { get; private set; } = true;
        public static bool HoverEnabled { get; private set; } = true;
        public static bool AIEnabled { get; private set; }
        public static int Generation { get; private set; }
        public static string Status { get; private set; } = "Initializing...";
        public static event Action Changed;
        public static bool IsCore => Mode == PenguinBackend.Core;
        public static bool IsReady => backend != null && (!IsCore || Core?.IsReady == true);
        /// <summary>True once the full index and occurrence scan finished for the current workspace.</summary>
        public static bool IsIndexComplete { get; private set; }
        public static void IndexComplete() => IsIndexComplete = true;

        public static void Reset(PenguinOptionsPage options)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            Generation++;
            backend = null; Core = null; IsIndexComplete = false;
            Mode = options.Backend; CompletionEnabled = options.EnableCompletionSuggestions;
            HoverEnabled = options.EnableHoverInfo; AIEnabled = options.EnableLocalAI;
            Report(Mode + " starting...");
        }
        public static void Activate(CoreClientService core, string engineRoot)
        {
            Core = core; backend = new CoreBackend(core, engineRoot); Report("Core ready");
        }
        public static void Activate(CacheService cache) { backend = new LegacyBackend(cache); Report("Legacy ready"); }
        public static void Report(string message) { Status = message; Changed?.Invoke(); }
        public static async Task<List<UnrealSymbol>> SearchAsync(string query, int limit, CancellationToken token)
        {
            var current = backend; var generation = Generation;
            if (current == null) return new List<UnrealSymbol>();
            var result = await current.SearchAsync(query, limit, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (generation != Generation) throw new OperationCanceledException();
            return result;
        }
        /// <summary>Every indexed symbol named exactly <paramref name="name"/> (not capped by fuzzy-search ranking).</summary>
        public static async Task<List<UnrealSymbol>> ExactAsync(string name, CancellationToken token)
        {
            if (string.IsNullOrEmpty(name)) return new List<UnrealSymbol>();
            if (!IsCore)
            {
                var cache = CacheService.Instance;
                if (backend == null || cache == null) return new List<UnrealSymbol>();
                return await Task.Run(() => cache.GetByExactName(name).Where(s => s.Name == name).ToList(), token).ConfigureAwait(false);
            }
            return (await SearchAsync(name, 200, token).ConfigureAwait(false)).Where(s => s.Name == name).ToList();
        }
        public static Task<UnrealClassInfo> ClassInfoAsync(UnrealSymbol symbol, CancellationToken token) =>
            backend?.ClassInfoAsync(symbol, token) ?? Task.FromResult<UnrealClassInfo>(null);
        public static Task<InheritanceInfo> InheritanceAsync(UnrealSymbol symbol, CancellationToken token) =>
            backend?.InheritanceAsync(symbol, token) ?? Task.FromResult(new InheritanceInfo());

        /// <summary>
        /// Reference candidates for <paramref name="symbol"/> with certainty assigned. Name-based, not semantic:
        /// a hit is certain when it is the declaration, is qualified with the owner, or the name is unambiguous.
        /// </summary>
        public static async Task<List<SymbolOccurrence>> ReferencesAsync(UnrealSymbol symbol, CancellationToken token,
            IDictionary<string, string> liveBuffers = null)
        {
            var current = backend; var generation = Generation;
            if (current == null || symbol == null) return new List<SymbolOccurrence>();
            var occurrences = await current.OccurrencesAsync(symbol, token).ConfigureAwait(false);

            // Unsaved editor text beats the on-disk index for files that are open.
            if (liveBuffers != null)
                foreach (var live in liveBuffers)
                {
                    occurrences.RemoveAll(o => SamePath(o.FilePath, live.Key));
                    foreach (var o in OccurrenceScanner.ScanFor(live.Value, symbol.Name)) { o.FilePath = live.Key; occurrences.Add(o); }
                }
            FillLineText(occurrences, liveBuffers);
            var namesakes = await ExactAsync(symbol.Name, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (generation != Generation) throw new OperationCanceledException();

            bool unique = namesakes.Count <= 1 || namesakes.All(s => s.OwnerClass == symbol.OwnerClass && s.Kind == symbol.Kind);
            var ownerStem = OwnerStem(symbol.OwnerClass);
            foreach (var o in occurrences)
            {
                if (SamePath(o.FilePath, symbol.FilePath) && o.LineNumber >= symbol.LineNumber && o.LineNumber <= symbol.LineNumber + 3)
                    o.Context = OccurrenceContext.Declaration;

                if (o.Context == OccurrenceContext.Declaration) o.IsCertain = true;
                else if (o.Context == OccurrenceContext.Qualified && !string.IsNullOrEmpty(o.Qualifier))
                    o.IsCertain = o.Qualifier == symbol.OwnerClass || (string.IsNullOrEmpty(symbol.OwnerClass) && unique);
                else if (unique) o.IsCertain = true;
                else if (ownerStem != null)
                    // A member used inside its owner's own header/source is almost always the owner's member.
                    o.IsCertain = o.Context != OccurrenceContext.MemberAccess &&
                        string.Equals(System.IO.Path.GetFileNameWithoutExtension(o.FilePath), ownerStem, StringComparison.OrdinalIgnoreCase);
            }
            // Qualified with a different known owner: definitely another symbol.
            occurrences.RemoveAll(o => o.Context == OccurrenceContext.Qualified && !o.IsCertain && !string.IsNullOrEmpty(symbol.OwnerClass) &&
                namesakes.Any(s => s.OwnerClass == o.Qualifier));
            return occurrences;
        }

        private static void FillLineText(List<SymbolOccurrence> occurrences, IDictionary<string, string> liveBuffers)
        {
            foreach (var group in occurrences.GroupBy(o => o.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                string[] lines = null;
                var live = liveBuffers?.FirstOrDefault(b => SamePath(b.Key, group.Key)).Value;
                try { lines = live != null ? live.Split('\n') : System.IO.File.ReadAllLines(group.Key); }
                catch (Exception ex) { PenguinLog.Error("References", group.Key + ": " + ex.Message); }
                if (lines == null) continue;
                foreach (var o in group)
                    if (o.LineNumber >= 1 && o.LineNumber <= lines.Length) o.LineText = lines[o.LineNumber - 1].TrimEnd('\r');
            }
            occurrences.Sort((a, b) =>
            {
                int c = string.Compare(a.FilePath, b.FilePath, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                c = a.LineNumber.CompareTo(b.LineNumber);
                return c != 0 ? c : a.ColumnNumber.CompareTo(b.ColumnNumber);
            });
        }

        /// <summary>UE file stem for an owner class: AMyCharacter → MyCharacter.</summary>
        internal static string OwnerStem(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return null;
            return owner.Length > 1 && "AUFSIET".IndexOf(owner[0]) >= 0 && char.IsUpper(owner[1]) ? owner.Substring(1) : owner;
        }

        private static bool SamePath(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) &&
            string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        public static void RecordUsage(UnrealSymbol symbol) { if (!IsCore) CacheService.Instance?.RecordUsage(symbol.Id); }
        public static async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PenguinLog.Error("Backend", ex); Report("Penguin: " + ex.Message); }
        }
    }
}
