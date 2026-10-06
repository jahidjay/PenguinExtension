using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PenguinExtention.Database;
using PenguinExtention.Models;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Thread-safe in-memory cache that sits between consumers (completion, hover, explorer)
    /// and the SQLite persistence layer.  All runtime reads are served from memory;
    /// writes are forwarded through to <see cref="SQLiteCache"/>.
    /// </summary>
    internal sealed class CacheService
    {
        // ── Singleton ───────────────────────────────────────────────
        private static CacheService _instance;
        public static CacheService Instance => _instance;

        public static void Initialize(SQLiteCache db)
        {
            _instance = new CacheService(db);
        }

        // ── State ───────────────────────────────────────────────────
        private readonly SQLiteCache _db;
        private readonly ReaderWriterLockSlim _lock = new ReaderWriterLockSlim();

        // Primary lookup: lowercase name → symbols with that name
        private Dictionary<string, List<UnrealSymbol>> _symbolsByName = new Dictionary<string, List<UnrealSymbol>>(StringComparer.OrdinalIgnoreCase);

        // Sorted names for binary-search prefix matching
        private List<string> _sortedNames = new List<string>();

        // Class metadata: class name → info
        private readonly ConcurrentDictionary<string, UnrealClassInfo> _classInfo = new ConcurrentDictionary<string, UnrealClassInfo>(StringComparer.OrdinalIgnoreCase);

        // All symbols flat list (for UI browsing / full scans)
        private List<UnrealSymbol> _allSymbols = new List<UnrealSymbol>();

        // Id → symbol for O(1) usage tracking (only symbols with a DB id)
        private Dictionary<long, UnrealSymbol> _byId = new Dictionary<long, UnrealSymbol>();

        /// <summary>Raised when the cache has been hydrated from SQLite and is ready for queries.</summary>
        public event EventHandler CacheReady;

        public bool IsLoaded { get; private set; }
        public int SymbolCount => _allSymbols.Count;

        private CacheService(SQLiteCache db)
        {
            _db = db;
        }

        // ── Bulk load from SQLite (startup) ─────────────────────────

        public async Task LoadFromDatabaseAsync()
        {
            var symbols = await _db.LoadAllSymbolsAsync().ConfigureAwait(false);
            var classInfos = await _db.LoadAllClassInfoAsync().ConfigureAwait(false);

            _lock.EnterWriteLock();
            try
            {
                _allSymbols = symbols;
                RebuildLookups();
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            foreach (var ci in classInfos)
            {
                _classInfo[ci.ClassName] = ci;
            }

            // Build inheritance chains
            BuildInheritanceChains();

            IsLoaded = true;
            CacheReady?.Invoke(this, EventArgs.Empty);
        }

        // ── Read operations (lock-free under read lock) ─────────────

        /// <summary>
        /// Fast prefix search.  Uses binary search on sorted names for O(log n + k) performance.
        /// Returns up to <paramref name="limit"/> results, project symbols first, sorted by usage.
        /// </summary>
        public List<UnrealSymbol> Search(string prefix, UnrealSymbolKind? kindFilter = null, int limit = 50)
        {
            if (string.IsNullOrEmpty(prefix) || !IsLoaded)
                return new List<UnrealSymbol>();

            _lock.EnterReadLock();
            try
            {
                var matchingNames = FindNamesByPrefix(prefix);
                var results = new List<UnrealSymbol>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                void Collect(string name)
                {
                    if (!seen.Add(name)) return;
                    if (_symbolsByName.TryGetValue(name, out var symbols))
                    {
                        foreach (var s in symbols)
                        {
                            if (kindFilter.HasValue && s.Kind != kindFilter.Value)
                                continue;
                            results.Add(s);
                        }
                    }
                }

                foreach (var name in matchingNames)
                    Collect(name);

                // Camel-hump fallback (ponytail: linear scan, only when prefix results are sparse)
                if (results.Count < limit && prefix.Length >= 2)
                {
                    foreach (var name in _sortedNames)
                    {
                        if (results.Count >= limit * 4) break;
                        if (IsCamelHumpMatch(prefix, name)) Collect(name);
                    }
                }

                // Project symbols first, then by access count descending, then alphabetical
                return results
                    .OrderBy(s => s.IsEngineSymbol ? 1 : 0)
                    .ThenByDescending(s => s.AccessCount)
                    .ThenBy(s => s.Name)
                    .Take(limit)
                    .ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>Exact name lookup.  Returns all symbols with the given name.</summary>
        public List<UnrealSymbol> GetByExactName(string name)
        {
            if (string.IsNullOrEmpty(name) || !IsLoaded)
                return new List<UnrealSymbol>();

            _lock.EnterReadLock();
            try
            {
                return _symbolsByName.TryGetValue(name, out var list)
                    ? list.ToList()
                    : new List<UnrealSymbol>();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>Get class metadata (base class, meta specifiers, inheritance chain).</summary>
        public UnrealClassInfo GetClassInfo(string className)
        {
            if (string.IsNullOrEmpty(className)) return null;
            _classInfo.TryGetValue(className, out var info);
            return info;
        }

        /// <summary>Get all symbols for UI browsing, optionally filtered.</summary>
        public List<UnrealSymbol> GetAllSymbols(UnrealSymbolKind? kindFilter = null, bool? engineFilter = null, int limit = 1000)
        {
            _lock.EnterReadLock();
            try
            {
                IEnumerable<UnrealSymbol> query = _allSymbols;

                if (kindFilter.HasValue)
                    query = query.Where(s => s.Kind == kindFilter.Value);

                if (engineFilter.HasValue)
                    query = query.Where(s => s.IsEngineSymbol == engineFilter.Value);

                return query.Take(limit).ToList();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        // ── Write operations (called by indexer) ────────────────────

        /// <summary>
        /// Merges new/updated symbols into the cache and writes through to SQLite.
        /// Called by the indexer after scanning a file.
        /// </summary>
        public async Task UpdateSymbolsAsync(string filePath, IReadOnlyList<UnrealSymbol> newSymbols, IReadOnlyList<UnrealClassInfo> newClassInfos = null)
        {
            // Remove stale symbols for this file from DB
            await _db.DeleteSymbolsByFileAsync(filePath).ConfigureAwait(false);

            // Insert new symbols
            int written = await _db.UpsertSymbolsBatchAsync(newSymbols).ConfigureAwait(false);

            // For class info, we need the new symbol IDs.
            // Re-query them from DB to get auto-generated IDs.
            if (newClassInfos != null && newClassInfos.Count > 0)
            {
                var dbSymbols = await _db.GetSymbolsByNameAsync(newClassInfos[0].ClassName).ConfigureAwait(false);
                var classInfosWithIds = new List<UnrealClassInfo>();

                foreach (var ci in newClassInfos)
                {
                    var dbSym = dbSymbols.FirstOrDefault(s => s.Name == ci.ClassName)
                                ?? (await _db.GetSymbolsByNameAsync(ci.ClassName).ConfigureAwait(false)).FirstOrDefault();
                    if (dbSym != null)
                    {
                        ci.SymbolId = dbSym.Id;
                        classInfosWithIds.Add(ci);
                    }
                }

                await _db.UpsertClassInfoBatchAsync(classInfosWithIds).ConfigureAwait(false);
            }

            // Update in-memory cache
            _lock.EnterWriteLock();
            try
            {
                ReplaceFileSymbols(filePath, newSymbols);
            }
            finally
            {
                _lock.ExitWriteLock();
            }

            // Update class info cache
            if (newClassInfos != null)
            {
                foreach (var ci in newClassInfos)
                {
                    _classInfo[ci.ClassName] = ci;
                }
                BuildInheritanceChains();
            }
        }

        /// <summary>Remove all symbols from a deleted file.</summary>
        public async Task RemoveFileSymbolsAsync(string filePath)
        {
            await _db.DeleteSymbolsByFileAsync(filePath).ConfigureAwait(false);
            await _db.DeleteIndexedFileAsync(filePath).ConfigureAwait(false);

            _lock.EnterWriteLock();
            try
            {
                ReplaceFileSymbols(filePath, new UnrealSymbol[0]);
            }
            finally
            {
                _lock.ExitWriteLock();
            }
        }

        /// <summary>Record that the user accessed a symbol (for hot-symbol prioritization).</summary>
        public void RecordUsage(long symbolId)
        {
            // Fire-and-forget DB update
            Task.Run(async () =>
            {
                try { await _db.IncrementUsageAsync(symbolId).ConfigureAwait(false); }
                catch { /* best effort */ }
            });

            // Update in-memory count
            _lock.EnterReadLock();
            try
            {
                if (_byId.TryGetValue(symbolId, out var sym))
                    sym.IncrementAccess();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        // ── Internals ───────────────────────────────────────────────

        private void RebuildLookups()
        {
            var dict = new Dictionary<string, List<UnrealSymbol>>(StringComparer.OrdinalIgnoreCase);
            var byId = new Dictionary<long, UnrealSymbol>();
            foreach (var s in _allSymbols)
            {
                if (!dict.TryGetValue(s.Name, out var list))
                {
                    list = new List<UnrealSymbol>();
                    dict[s.Name] = list;
                }
                list.Add(s);
                if (s.Id != 0) byId[s.Id] = s;
            }

            _symbolsByName = dict;
            _byId = byId;
            _sortedNames = dict.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Incrementally swaps one file's symbols; avoids re-sorting all names. Caller holds write lock.</summary>
        private void ReplaceFileSymbols(string filePath, IReadOnlyList<UnrealSymbol> added)
        {
            var removed = _allSymbols
                .Where(s => string.Equals(s.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _allSymbols.RemoveAll(s => string.Equals(s.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

            foreach (var r in removed)
            {
                if (r.Id != 0) _byId.Remove(r.Id);
                if (!_symbolsByName.TryGetValue(r.Name, out var list)) continue;
                list.Remove(r);
                if (list.Count == 0)
                {
                    _symbolsByName.Remove(r.Name);
                    int idx = _sortedNames.BinarySearch(r.Name, StringComparer.OrdinalIgnoreCase);
                    if (idx >= 0) _sortedNames.RemoveAt(idx);
                }
            }

            foreach (var a in added)
            {
                if (a.Id != 0) _byId[a.Id] = a;
                if (!_symbolsByName.TryGetValue(a.Name, out var list))
                {
                    list = new List<UnrealSymbol>();
                    _symbolsByName[a.Name] = list;
                    int idx = _sortedNames.BinarySearch(a.Name, StringComparer.OrdinalIgnoreCase);
                    _sortedNames.Insert(idx < 0 ? ~idx : idx, a.Name);
                }
                list.Add(a);
            }

            _allSymbols.AddRange(added);
        }

        /// <summary>
        /// Camel-hump match: first char matches, then each pattern char must hit an uppercase/underscore-start
        /// hump or continue the previous match. E.g. "AMyC" matches "AMyCharacter", "BP" matches "BeginPlay".
        /// </summary>
        private static bool IsCamelHumpMatch(string pattern, string name)
        {
            return HumpMatch(pattern, 0, name, 0, false);
        }

        private static bool HumpMatch(string p, int pi, string n, int ni, bool prevMatched)
        {
            if (pi == p.Length) return true;
            for (; ni < n.Length; ni++)
            {
                if (char.ToLowerInvariant(p[pi]) != char.ToLowerInvariant(n[ni])) { if (pi == 0) return false; prevMatched = false; continue; }
                bool isHump = ni == 0 || char.IsUpper(n[ni]) || n[ni - 1] == '_';
                bool contiguous = prevMatched && ni > 0;
                if (pi == 0 ? ni == 0 : (isHump || contiguous))
                {
                    if (HumpMatch(p, pi + 1, n, ni + 1, true)) return true;
                }
                if (pi == 0) return false;
                prevMatched = false;
            }
            return false;
        }

        /// <summary>Binary search for all names starting with <paramref name="prefix"/>.</summary>
        private IEnumerable<string> FindNamesByPrefix(string prefix)
        {
            int idx = _sortedNames.BinarySearch(prefix, StringComparer.OrdinalIgnoreCase);
            if (idx < 0) idx = ~idx; // insertion point

            while (idx < _sortedNames.Count && _sortedNames[idx].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                yield return _sortedNames[idx];
                idx++;
            }
        }

        private void BuildInheritanceChains()
        {
            foreach (var kvp in _classInfo)
            {
                var chain = new List<string>();
                var current = kvp.Value.BaseClass;
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                while (!string.IsNullOrEmpty(current) && visited.Add(current))
                {
                    chain.Add(current);
                    if (_classInfo.TryGetValue(current, out var parentInfo))
                        current = parentInfo.BaseClass;
                    else
                        break;
                }

                kvp.Value.InheritanceChain = chain;
            }
        }
    }
}
