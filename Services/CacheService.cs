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

        // Acronym keys → names, kept in step with _sortedNames under the same lock
        private readonly AcronymIndex _acronyms = new AcronymIndex();

        // Class metadata: class name → info
        private readonly ConcurrentDictionary<string, UnrealClassInfo> _classInfo = new ConcurrentDictionary<string, UnrealClassInfo>(StringComparer.OrdinalIgnoreCase);

        // All symbols flat list (for UI browsing / full scans)
        private List<UnrealSymbol> _allSymbols = new List<UnrealSymbol>();

        // Id → symbol for O(1) usage tracking (only symbols with a DB id)
        private Dictionary<long, UnrealSymbol> _byId = new Dictionary<long, UnrealSymbol>();

        // Bumped (under write lock) whenever a name is added to or removed from the index.
        private int _namesVersion;
        // Bumped (under write lock) whenever any symbol is added, removed or replaced.
        private int _symbolsVersion;
        private volatile HashSet<string> _namesSnapshot;
        private volatile int _namesSnapshotVersion = -1;

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

                // Acronym matches ("CMC" → UCharacterMovementComponent) via the sorted acronym index.
                var acronymHits = new HashSet<string>(StringComparer.Ordinal);
                if (results.Count < limit && prefix.Length >= 2)
                {
                    foreach (var name in _acronyms.Find(prefix, limit * 4))
                    {
                        if (results.Count >= limit * 4) break;
                        acronymHits.Add(name);
                        Collect(name);
                    }
                }

                // Exact name, then prefix, then acronym hits; then project symbols, usage, alphabetical
                return results
                    .OrderBy(s => string.Equals(s.Name, prefix, StringComparison.Ordinal) ? 0
                        : string.Equals(s.Name, prefix, StringComparison.OrdinalIgnoreCase) ? 1
                        : s.Name.StartsWith(prefix, StringComparison.Ordinal) ? 2
                        : s.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? 3
                        : acronymHits.Contains(s.Name) ? 4 : 5)
                    .ThenBy(s => s.IsEngineSymbol ? 1 : 0)
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

        /// <summary>
        /// Visual Assist-style search: prefix and acronym hits from <see cref="Search"/>, plus camel-hump and
        /// substring matches ("GetActLoc" → GetActorLocation, "cmc" → UCharacterMovementComponent). Best match first.
        /// </summary>
        public List<UnrealSymbol> SearchFuzzy(string typed, int limit)
        {
            if (string.IsNullOrEmpty(typed) || !IsLoaded) return new List<UnrealSymbol>();
            var results = Search(typed, null, limit);
            if (typed.Length < 2 || results.Count >= limit) return results;

            var seen = new HashSet<string>(results.Select(s => s.Name), StringComparer.Ordinal);
            var extra = new List<(int score, UnrealSymbol symbol)>();
            _lock.EnterReadLock();
            try
            {
                foreach (var name in _sortedNames)
                {
                    if (seen.Contains(name)) continue;
                    int score = Completion.PenguinMatcher.Score(typed, name);
                    if (score < 3) continue; // prefix matches already came from Search
                    if (!_symbolsByName.TryGetValue(name, out var list)) continue;
                    foreach (var s in list) extra.Add((score, s));
                }
            }
            finally
            {
                _lock.ExitReadLock();
            }
            results.AddRange(extra
                .OrderBy(e => e.score).ThenBy(e => e.symbol.IsEngineSymbol ? 1 : 0).ThenByDescending(e => e.symbol.AccessCount).ThenBy(e => e.symbol.Name.Length)
                .Select(e => e.symbol).Take(limit - results.Count));
            return results;
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

        /// <summary>
        /// Case-sensitive snapshot of every indexed symbol name, plus a version that changes whenever the
        /// name set changes. The snapshot is immutable once returned; callers must not mutate it.
        /// </summary>
        public HashSet<string> GetKnownNames(out int version)
        {
            _lock.EnterReadLock();
            try
            {
                version = _namesVersion;
                var cached = _namesSnapshot;
                if (cached != null && _namesSnapshotVersion == version) return cached;

                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var s in _allSymbols) names.Add(s.Name);
                // Benign race: two readers may build equal snapshots; last writer wins.
                _namesSnapshot = names;
                _namesSnapshotVersion = version;
                return names;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        /// <summary>Coloring categories: 1 = type, 2 = function, 3 = macro, 4 = enum value.</summary>
        public Dictionary<string, byte> GetNameCategories(out int version)
        {
            _lock.EnterReadLock();
            try
            {
                version = _namesVersion;
                var cached = _categories;
                if (cached != null && _categoriesVersion == version) return cached;
                var map = new Dictionary<string, byte>(StringComparer.Ordinal);
                foreach (var s in _allSymbols)
                {
                    byte cat;
                    switch (s.Kind)
                    {
                        case UnrealSymbolKind.Class: case UnrealSymbolKind.Struct: case UnrealSymbolKind.Interface:
                        case UnrealSymbolKind.Enum: case UnrealSymbolKind.Typedef: case UnrealSymbolKind.Delegate: cat = 1; break;
                        case UnrealSymbolKind.Function: cat = 2; break;
                        case UnrealSymbolKind.Macro: cat = 3; break;
                        case UnrealSymbolKind.EnumValue: cat = 4; break;
                        default: continue;
                    }
                    // Types win over functions (constructors share the class name).
                    if (!map.TryGetValue(s.Name, out var existing) || cat < existing) map[s.Name] = cat;
                }
                _categories = map;
                _categoriesVersion = version;
                return map;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        private volatile Dictionary<string, byte> _categories;
        private volatile int _categoriesVersion = -1;

        /// <summary>Members (functions, properties, enum values …) whose OwnerClass is <paramref name="owner"/>.</summary>
        public List<UnrealSymbol> GetMembers(string owner)
        {
            if (string.IsNullOrEmpty(owner) || !IsLoaded) return new List<UnrealSymbol>();
            _lock.EnterReadLock();
            try
            {
                var map = _members;
                if (map == null || _membersVersion != _symbolsVersion)
                {
                    // Benign race: two readers may build equal maps; last writer wins.
                    map = new Dictionary<string, List<UnrealSymbol>>(StringComparer.Ordinal);
                    foreach (var s in _allSymbols)
                    {
                        if (string.IsNullOrEmpty(s.OwnerClass)) continue;
                        if (!map.TryGetValue(s.OwnerClass, out var list)) map[s.OwnerClass] = list = new List<UnrealSymbol>();
                        list.Add(s);
                    }
                    _members = map;
                    _membersVersion = _symbolsVersion;
                }
                return map.TryGetValue(owner, out var found) ? found.ToList() : new List<UnrealSymbol>();
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        private volatile Dictionary<string, List<UnrealSymbol>> _members;
        private volatile int _membersVersion = -1;

        /// <summary>Name-based occurrence candidates recorded by the occurrence pass.</summary>
        public Task<List<SymbolOccurrence>> GetOccurrencesAsync(string name) => _db.GetOccurrencesAsync(name);

        /// <summary>The backing database (Self-Test row counts).</summary>
        internal SQLiteCache Database => _db;

        /// <summary>Class/struct symbols whose direct base is <paramref name="className"/>.</summary>
        public List<UnrealSymbol> GetDerivedClasses(string className)
        {
            var derived = new List<UnrealSymbol>();
            if (string.IsNullOrEmpty(className)) return derived;
            foreach (var kvp in _classInfo)
            {
                if (string.Equals(kvp.Value.BaseClass, className, StringComparison.Ordinal))
                    derived.AddRange(GetByExactName(kvp.Key).Where(s => s.Kind == UnrealSymbolKind.Class ||
                        s.Kind == UnrealSymbolKind.Struct || s.Kind == UnrealSymbolKind.Interface));
            }
            return derived;
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
            _acronyms.Load(_sortedNames);
            _namesVersion++;
            _symbolsVersion++;
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
                    _acronyms.Remove(r.Name);
                    _namesVersion++;
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
                    _acronyms.Add(a.Name);
                    _namesVersion++;
                }
                list.Add(a);
            }

            _allSymbols.AddRange(added);
            _symbolsVersion++;
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
