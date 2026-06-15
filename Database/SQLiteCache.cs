using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SQLite;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PenguinExtention.Models;

namespace PenguinExtention.Database
{
    /// <summary>
    /// Thin wrapper around a SQLite database that stores the persistent Unreal symbol cache.
    /// Uses WAL journal mode for concurrent read access during background indexing.
    /// All write operations are serialized through <see cref="_writeLock"/>.
    /// </summary>
    internal sealed class SQLiteCache : IDisposable
    {
        private readonly string _dbPath;
        private SQLiteConnection _connection;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private bool _disposed;

        public string DatabasePath => _dbPath;
        public bool Exists => File.Exists(_dbPath);

        public SQLiteCache(string solutionDirectory)
        {
            var cacheDir = Path.Combine(solutionDirectory, ".vs", "PenguinExtension");
            Directory.CreateDirectory(cacheDir);
            _dbPath = Path.Combine(cacheDir, "penguin_cache.db");
        }

        // ── Lifecycle ───────────────────────────────────────────────

        public async Task InitializeAsync()
        {
            var csb = new SQLiteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Version = 3,
                JournalMode = SQLiteJournalModeEnum.Wal,
                SyncMode = SynchronizationModes.Normal,
                CacheSize = 10000,
                ForeignKeys = true
            };

            _connection = new SQLiteConnection(csb.ToString());
            await _connection.OpenAsync().ConfigureAwait(false);

            // Additional PRAGMAs for performance
            await ExecuteNonQueryAsync("PRAGMA temp_store = MEMORY;").ConfigureAwait(false);
            await CreateSchemaAsync().ConfigureAwait(false);
        }

        private async Task CreateSchemaAsync()
        {
            const string schema = @"
                CREATE TABLE IF NOT EXISTS indexed_files (
                    file_path     TEXT    PRIMARY KEY,
                    content_hash  TEXT    NOT NULL,
                    last_indexed  TEXT    NOT NULL,
                    is_engine     INTEGER NOT NULL DEFAULT 0
                );

                CREATE TABLE IF NOT EXISTS symbols (
                    id                   INTEGER PRIMARY KEY AUTOINCREMENT,
                    name                 TEXT    NOT NULL,
                    fully_qualified_name TEXT,
                    kind                 INTEGER NOT NULL,
                    file_path            TEXT    NOT NULL,
                    line_number          INTEGER NOT NULL,
                    column_number        INTEGER NOT NULL DEFAULT 0,
                    owner_class          TEXT,
                    signature            TEXT,
                    return_type          TEXT,
                    comment              TEXT,
                    is_engine            INTEGER NOT NULL DEFAULT 0,
                    last_indexed         TEXT    NOT NULL
                );

                CREATE TABLE IF NOT EXISTS class_info (
                    symbol_id       INTEGER PRIMARY KEY,
                    base_class      TEXT,
                    module          TEXT,
                    meta_specifiers TEXT,
                    is_abstract     INTEGER NOT NULL DEFAULT 0,
                    FOREIGN KEY (symbol_id) REFERENCES symbols(id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS symbol_usage (
                    symbol_id     INTEGER PRIMARY KEY,
                    access_count  INTEGER NOT NULL DEFAULT 0,
                    last_accessed TEXT,
                    FOREIGN KEY (symbol_id) REFERENCES symbols(id) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS idx_symbols_name      ON symbols(name);
                CREATE INDEX IF NOT EXISTS idx_symbols_kind       ON symbols(kind);
                CREATE INDEX IF NOT EXISTS idx_symbols_owner      ON symbols(owner_class);
                CREATE INDEX IF NOT EXISTS idx_symbols_file       ON symbols(file_path);
                CREATE INDEX IF NOT EXISTS idx_symbols_name_kind  ON symbols(name, kind);
            ";

            await ExecuteNonQueryAsync(schema).ConfigureAwait(false);
        }

        // ── Bulk reads (startup) ────────────────────────────────────

        /// <summary>
        /// Reads every symbol row into memory.  Called once at startup for fast cache hydration.
        /// </summary>
        public async Task<List<UnrealSymbol>> LoadAllSymbolsAsync()
        {
            var symbols = new List<UnrealSymbol>(200_000);

            const string sql = @"
                SELECT s.*, COALESCE(u.access_count, 0) AS access_count
                FROM symbols s
                LEFT JOIN symbol_usage u ON u.symbol_id = s.id
                ORDER BY s.name";

            using (var cmd = new SQLiteCommand(sql, _connection))
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    symbols.Add(ReadSymbol((SQLiteDataReader)reader));
                }
            }

            return symbols;
        }

        /// <summary>Reads all class_info rows.</summary>
        public async Task<List<UnrealClassInfo>> LoadAllClassInfoAsync()
        {
            var infos = new List<UnrealClassInfo>(50_000);

            const string sql = @"
                SELECT ci.*, s.name AS class_name
                FROM class_info ci
                JOIN symbols s ON s.id = ci.symbol_id
                ORDER BY s.name";

            using (var cmd = new SQLiteCommand(sql, _connection))
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    infos.Add(new UnrealClassInfo
                    {
                        SymbolId = reader.GetInt64(reader.GetOrdinal("symbol_id")),
                        ClassName = reader.GetString(reader.GetOrdinal("class_name")),
                        BaseClass = reader.IsDBNull(reader.GetOrdinal("base_class")) ? null : reader.GetString(reader.GetOrdinal("base_class")),
                        Module = reader.IsDBNull(reader.GetOrdinal("module")) ? null : reader.GetString(reader.GetOrdinal("module")),
                        MetaSpecifiers = reader.IsDBNull(reader.GetOrdinal("meta_specifiers")) ? null : reader.GetString(reader.GetOrdinal("meta_specifiers")),
                        IsAbstract = reader.GetInt32(reader.GetOrdinal("is_abstract")) != 0
                    });
                }
            }

            return infos;
        }

        // ── Querying ────────────────────────────────────────────────

        /// <summary>Prefix search with optional kind filter.  Used as a fallback when the in-memory cache is not yet loaded.</summary>
        public async Task<List<UnrealSymbol>> SearchSymbolsAsync(string prefix, UnrealSymbolKind? kind = null, int limit = 50)
        {
            var results = new List<UnrealSymbol>();
            var sql = "SELECT s.*, COALESCE(u.access_count, 0) AS access_count FROM symbols s LEFT JOIN symbol_usage u ON u.symbol_id = s.id WHERE s.name LIKE @prefix";

            if (kind.HasValue)
                sql += " AND s.kind = @kind";

            sql += " ORDER BY COALESCE(u.access_count, 0) DESC, s.name LIMIT @limit";

            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@prefix", prefix + "%");
                if (kind.HasValue)
                    cmd.Parameters.AddWithValue("@kind", (int)kind.Value);
                cmd.Parameters.AddWithValue("@limit", limit);

                using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        results.Add(ReadSymbol((SQLiteDataReader)reader));
                    }
                }
            }

            return results;
        }

        /// <summary>Exact name lookup.</summary>
        public async Task<List<UnrealSymbol>> GetSymbolsByNameAsync(string name)
        {
            var results = new List<UnrealSymbol>();

            const string sql = @"
                SELECT s.*, COALESCE(u.access_count, 0) AS access_count
                FROM symbols s
                LEFT JOIN symbol_usage u ON u.symbol_id = s.id
                WHERE s.name = @name";

            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@name", name);
                using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        results.Add(ReadSymbol((SQLiteDataReader)reader));
                    }
                }
            }

            return results;
        }

        // ── Indexed-file tracking ───────────────────────────────────

        public async Task<IndexedFile> GetIndexedFileAsync(string filePath)
        {
            const string sql = "SELECT * FROM indexed_files WHERE file_path = @path";

            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                cmd.Parameters.AddWithValue("@path", filePath);
                using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    if (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        return new IndexedFile
                        {
                            FilePath = reader.GetString(reader.GetOrdinal("file_path")),
                            ContentHash = reader.GetString(reader.GetOrdinal("content_hash")),
                            LastIndexed = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_indexed"))),
                            IsEngineFile = reader.GetInt32(reader.GetOrdinal("is_engine")) != 0
                        };
                    }
                }
            }

            return null;
        }

        public async Task UpsertIndexedFileAsync(IndexedFile file)
        {
            const string sql = @"
                INSERT OR REPLACE INTO indexed_files (file_path, content_hash, last_indexed, is_engine)
                VALUES (@path, @hash, @indexed, @engine)";

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var cmd = new SQLiteCommand(sql, _connection))
                {
                    cmd.Parameters.AddWithValue("@path", file.FilePath);
                    cmd.Parameters.AddWithValue("@hash", file.ContentHash);
                    cmd.Parameters.AddWithValue("@indexed", file.LastIndexed.ToString("o"));
                    cmd.Parameters.AddWithValue("@engine", file.IsEngineFile ? 1 : 0);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // ── Symbol writes (indexing) ────────────────────────────────

        /// <summary>
        /// Batch upsert symbols inside a single transaction for maximum throughput.
        /// Returns the number of symbols written.
        /// </summary>
        public async Task<int> UpsertSymbolsBatchAsync(IReadOnlyList<UnrealSymbol> symbols)
        {
            if (symbols == null || symbols.Count == 0) return 0;

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var txn = _connection.BeginTransaction())
                {
                    const string sql = @"
                        INSERT INTO symbols (name, fully_qualified_name, kind, file_path, line_number, column_number,
                                            owner_class, signature, return_type, comment, is_engine, last_indexed)
                        VALUES (@name, @fqn, @kind, @file, @line, @col, @owner, @sig, @ret, @cmt, @eng, @idx)";

                    using (var cmd = new SQLiteCommand(sql, _connection))
                    {
                        cmd.Transaction = txn;

                        // Pre-create parameters once
                        var pName = cmd.Parameters.Add("@name", DbType.String);
                        var pFqn  = cmd.Parameters.Add("@fqn",  DbType.String);
                        var pKind = cmd.Parameters.Add("@kind", DbType.Int32);
                        var pFile = cmd.Parameters.Add("@file", DbType.String);
                        var pLine = cmd.Parameters.Add("@line", DbType.Int32);
                        var pCol  = cmd.Parameters.Add("@col",  DbType.Int32);
                        var pOwn  = cmd.Parameters.Add("@owner",DbType.String);
                        var pSig  = cmd.Parameters.Add("@sig",  DbType.String);
                        var pRet  = cmd.Parameters.Add("@ret",  DbType.String);
                        var pCmt  = cmd.Parameters.Add("@cmt",  DbType.String);
                        var pEng  = cmd.Parameters.Add("@eng",  DbType.Int32);
                        var pIdx  = cmd.Parameters.Add("@idx",  DbType.String);

                        foreach (var s in symbols)
                        {
                            pName.Value = s.Name;
                            pFqn.Value  = (object)s.FullyQualifiedName ?? DBNull.Value;
                            pKind.Value = (int)s.Kind;
                            pFile.Value = s.FilePath;
                            pLine.Value = s.LineNumber;
                            pCol.Value  = s.ColumnNumber;
                            pOwn.Value  = (object)s.OwnerClass ?? DBNull.Value;
                            pSig.Value  = (object)s.Signature ?? DBNull.Value;
                            pRet.Value  = (object)s.ReturnType ?? DBNull.Value;
                            pCmt.Value  = (object)s.Comment ?? DBNull.Value;
                            pEng.Value  = s.IsEngineSymbol ? 1 : 0;
                            pIdx.Value  = s.LastIndexed.ToString("o");

                            cmd.ExecuteNonQuery();
                        }
                    }

                    txn.Commit();
                }

                return symbols.Count;
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>Batch upsert class_info rows.</summary>
        public async Task UpsertClassInfoBatchAsync(IReadOnlyList<UnrealClassInfo> infos)
        {
            if (infos == null || infos.Count == 0) return;

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var txn = _connection.BeginTransaction())
                {
                    const string sql = @"
                        INSERT OR REPLACE INTO class_info (symbol_id, base_class, module, meta_specifiers, is_abstract)
                        VALUES (@sid, @base, @mod, @meta, @abs)";

                    using (var cmd = new SQLiteCommand(sql, _connection))
                    {
                        cmd.Transaction = txn;

                        var pSid  = cmd.Parameters.Add("@sid",  DbType.Int64);
                        var pBase = cmd.Parameters.Add("@base", DbType.String);
                        var pMod  = cmd.Parameters.Add("@mod",  DbType.String);
                        var pMeta = cmd.Parameters.Add("@meta", DbType.String);
                        var pAbs  = cmd.Parameters.Add("@abs",  DbType.Int32);

                        foreach (var ci in infos)
                        {
                            pSid.Value  = ci.SymbolId;
                            pBase.Value = (object)ci.BaseClass ?? DBNull.Value;
                            pMod.Value  = (object)ci.Module ?? DBNull.Value;
                            pMeta.Value = (object)ci.MetaSpecifiers ?? DBNull.Value;
                            pAbs.Value  = ci.IsAbstract ? 1 : 0;

                            cmd.ExecuteNonQuery();
                        }
                    }

                    txn.Commit();
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>Removes all symbols that belong to a given source file. Called before re-indexing a file.</summary>
        public async Task DeleteSymbolsByFileAsync(string filePath)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                const string sql = "DELETE FROM symbols WHERE file_path = @path";
                using (var cmd = new SQLiteCommand(sql, _connection))
                {
                    cmd.Parameters.AddWithValue("@path", filePath);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>Removes the indexed_files entry for a deleted file.</summary>
        public async Task DeleteIndexedFileAsync(string filePath)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                const string sql = "DELETE FROM indexed_files WHERE file_path = @path";
                using (var cmd = new SQLiteCommand(sql, _connection))
                {
                    cmd.Parameters.AddWithValue("@path", filePath);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // ── Usage tracking ──────────────────────────────────────────

        public async Task IncrementUsageAsync(long symbolId)
        {
            const string sql = @"
                INSERT INTO symbol_usage (symbol_id, access_count, last_accessed)
                VALUES (@id, 1, @now)
                ON CONFLICT(symbol_id) DO UPDATE SET
                    access_count = access_count + 1,
                    last_accessed = @now";

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var cmd = new SQLiteCommand(sql, _connection))
                {
                    cmd.Parameters.AddWithValue("@id", symbolId);
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>Returns the total number of symbols in the database.</summary>
        public async Task<long> GetSymbolCountAsync()
        {
            const string sql = "SELECT COUNT(*) FROM symbols";
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return Convert.ToInt64(result);
            }
        }

        // ── Helpers ─────────────────────────────────────────────────

        private static UnrealSymbol ReadSymbol(SQLiteDataReader reader)
        {
            return new UnrealSymbol
            {
                Id                 = reader.GetInt64(reader.GetOrdinal("id")),
                Name               = reader.GetString(reader.GetOrdinal("name")),
                FullyQualifiedName = reader.IsDBNull(reader.GetOrdinal("fully_qualified_name")) ? null : reader.GetString(reader.GetOrdinal("fully_qualified_name")),
                Kind               = (UnrealSymbolKind)reader.GetInt32(reader.GetOrdinal("kind")),
                FilePath           = reader.GetString(reader.GetOrdinal("file_path")),
                LineNumber         = reader.GetInt32(reader.GetOrdinal("line_number")),
                ColumnNumber       = reader.GetInt32(reader.GetOrdinal("column_number")),
                OwnerClass         = reader.IsDBNull(reader.GetOrdinal("owner_class")) ? null : reader.GetString(reader.GetOrdinal("owner_class")),
                Signature          = reader.IsDBNull(reader.GetOrdinal("signature")) ? null : reader.GetString(reader.GetOrdinal("signature")),
                ReturnType         = reader.IsDBNull(reader.GetOrdinal("return_type")) ? null : reader.GetString(reader.GetOrdinal("return_type")),
                Comment            = reader.IsDBNull(reader.GetOrdinal("comment")) ? null : reader.GetString(reader.GetOrdinal("comment")),
                IsEngineSymbol     = reader.GetInt32(reader.GetOrdinal("is_engine")) != 0,
                LastIndexed        = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_indexed"))),
                AccessCount        = reader.GetOrdinal("access_count") >= 0 && !reader.IsDBNull(reader.GetOrdinal("access_count"))
                                        ? reader.GetInt32(reader.GetOrdinal("access_count")) : 0
            };
        }

        private async Task ExecuteNonQueryAsync(string sql)
        {
            using (var cmd = new SQLiteCommand(sql, _connection))
            {
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        // ── IDisposable ─────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _connection?.Close();
            _connection?.Dispose();
            _writeLock?.Dispose();
        }
    }
}
