using System;
using System.Diagnostics;
using System.Threading.Tasks;
using PenguinExtention.Database;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Orchestrates the startup cache loading sequence.
    /// Target: hydrate the in-memory <see cref="CacheService"/> from SQLite in under 2 seconds.
    /// </summary>
    internal sealed class StartupCacheLoader
    {
        private readonly SQLiteCache _db;
        private readonly CacheService _cache;

        public StartupCacheLoader(SQLiteCache db, CacheService cache)
        {
            _db = db;
            _cache = cache;
        }

        /// <summary>
        /// Initializes the database (creating schema if needed) and loads all cached symbols into memory.
        /// Returns the elapsed time for diagnostics.
        /// </summary>
        public async Task<TimeSpan> LoadAsync()
        {
            var sw = Stopwatch.StartNew();

            // Step 1: Initialize database connection and schema
            await _db.InitializeAsync().ConfigureAwait(false);

            // Step 2: Load all symbols into the in-memory cache
            await _cache.LoadFromDatabaseAsync().ConfigureAwait(false);

            sw.Stop();
            return sw.Elapsed;
        }

        /// <summary>
        /// Returns true if a cache database already exists on disk (i.e. this isn't a first run).
        /// </summary>
        public bool HasExistingCache => _db.Exists;
    }
}
