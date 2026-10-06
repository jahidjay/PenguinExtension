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
    }
    internal sealed class LegacyBackend : ISymbolBackend
    {
        private readonly CacheService cache;
        public LegacyBackend(CacheService cache) { this.cache = cache; }
        public Task<List<UnrealSymbol>> SearchAsync(string query, int limit, CancellationToken token) => Task.Run(() =>
            string.IsNullOrWhiteSpace(query) ? cache.GetAllSymbols(limit: limit) : cache.Search(query, null, limit), token);
        public Task<UnrealClassInfo> ClassInfoAsync(UnrealSymbol symbol, CancellationToken token) => Task.Run(() => cache.GetClassInfo(symbol.Name), token);
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

        public static void Reset(PenguinOptionsPage options)
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();
            Generation++;
            backend = null; Core = null;
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
        public static async Task<List<UnrealSymbol>> ExactAsync(string name, CancellationToken token) =>
            (await SearchAsync(name, 200, token).ConfigureAwait(false)).Where(s => s.Name == name).ToList();
        public static Task<UnrealClassInfo> ClassInfoAsync(UnrealSymbol symbol, CancellationToken token) =>
            backend?.ClassInfoAsync(symbol, token) ?? Task.FromResult<UnrealClassInfo>(null);
        public static void RecordUsage(UnrealSymbol symbol) { if (!IsCore) CacheService.Instance?.RecordUsage(symbol.Id); }
        public static async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Report("Penguin: " + ex.Message); }
        }
    }
}
