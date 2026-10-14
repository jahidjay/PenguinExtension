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
    /// Every operation opens its own pooled connection (SQLiteConnection is not thread-safe);
    /// all write operations are additionally serialized through <see cref="_writeLock"/>.
    /// </summary>
    internal sealed class SQLiteCache : IDisposable
    {
        /// <summary>Bump when the schema changes; migrations run in <see cref="MigrateAsync"/>.</summary>
        private const int SchemaVersion = 2;

        private readonly string _dbPath;
        private string _connectionString;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private volatile bool _disposed;


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
                ForeignKeys = true,
                Pooling = true
            };
            _connectionString = csb.ToString();

            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var conn = await OpenAsync().ConfigureAwait(false))
                {
                    await ExecuteNonQueryAsync(conn, SchemaV1).ConfigureAwait(false);
                    await MigrateAsync(conn).ConfigureAwait(false);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private const string SchemaV1 = @"
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

        // v2: name-based occurrence index used by Find References / Rename / highlight.
        private const string SchemaV2 = @"
            CREATE TABLE IF NOT EXISTS occurrences (
                symbol_name  TEXT    NOT NULL,
                file_path    TEXT    NOT NULL,
                line_number  INTEGER NOT NULL,
                column_number INTEGER NOT NULL,
                context      INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_occ_name ON occurrences(symbol_name);
            CREATE INDEX IF NOT EXISTS idx_occ_file ON occurrences(file_path);

            -- scan_key = content hash + known-name-set fingerprint; unchanged key means no rescan needed.
            CREATE TABLE IF NOT EXISTS occurrence_files (
                file_path TEXT PRIMARY KEY,
                scan_key  TEXT NOT NULL
            );
        ";

        private static async Task MigrateAsync(SQLiteConnection conn)
        {
            long version;
            using (var cmd = new SQLiteCommand("PRAGMA user_version;", conn))
                version = Convert.ToInt64(await cmd.ExecuteScalarAsync().ConfigureAwait(false));

            if (version >= SchemaVersion) return;
            if (version < 2)
            {
                await ExecuteNonQueryAsync(conn, SchemaV2).ConfigureAwait(false);
            }
            await ExecuteNonQueryAsync(conn, "PRAGMA user_version = " + SchemaVersion + ";").ConfigureAwait(false);
        }

        private async Task<SQLiteConnection> OpenAsync()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(SQLiteCache));
            if (_connectionString == null) throw new InvalidOperationException("SQLiteCache is not initialized.");
            var conn = new SQLiteConnection(_connectionString);
            try
            {
                await conn.OpenAsync().ConfigureAwait(false);
                using (var cmd = new SQLiteCommand("PRAGMA temp_store = MEMORY;", conn))
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                return conn;
            }
            catch
            {
                conn.Dispose();
                throw;
            }
        }

        private async Task WriteAsync(Func<SQLiteConnection, Task> action)
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var conn = await OpenAsync().ConfigureAwait(false))
                    await action(conn).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        // ── Bulk reads (startup) ────────────────────────────────────

        /// <summary>
        /// Reads every symbol row into memory.  Called once at startup for fast cache hydration.
        /// </summary>
        public Task<List<UnrealSymbol>> LoadAllSymbolsAsync()
        {
            const string sql = @"
                SELECT s.*, COALESCE(u.access_count, 0) AS access_count
                FROM symbols s
                LEFT JOIN symbol_usage u ON u.symbol_id = s.id
                ORDER BY s.name";

            return ReadSymbolsAsync(sql, null, 200_000);
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

            using (var conn = await OpenAsync().ConfigureAwait(false))
            using (var cmd = new SQLiteCommand(sql, conn))
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
            {
                int oId = reader.GetOrdinal("symbol_id"), oName = reader.GetOrdinal("class_name"),
                    oBase = reader.GetOrdinal("base_class"), oMod = reader.GetOrdinal("module"),
                    oMeta = reader.GetOrdinal("meta_specifiers"), oAbs = reader.GetOrdinal("is_abstract");

                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    infos.Add(new UnrealClassInfo
                    {
                        SymbolId = reader.GetInt64(oId),
                        ClassName = reader.GetString(oName),
                        BaseClass = reader.IsDBNull(oBase) ? null : reader.GetString(oBase),
                        Module = reader.IsDBNull(oMod) ? null : reader.GetString(oMod),
                        MetaSpecifiers = reader.IsDBNull(oMeta) ? null : reader.GetString(oMeta),
                        IsAbstract = reader.GetInt32(oAbs) != 0
                    });
                }
            }

            return infos;
        }

        // ── Querying ────────────────────────────────────────────────

        /// <summary>Exact name lookup.</summary>
        public Task<List<UnrealSymbol>> GetSymbolsByNameAsync(string name)
        {
            const string sql = @"
                SELECT s.*, COALESCE(u.access_count, 0) AS access_count
                FROM symbols s
                LEFT JOIN symbol_usage u ON u.symbol_id = s.id
                WHERE s.name = @name";

            return ReadSymbolsAsync(sql, cmd => cmd.Parameters.AddWithValue("@name", name));
        }

        // ── Indexed-file tracking ───────────────────────────────────

        public async Task<IndexedFile> GetIndexedFileAsync(string filePath)
        {
            const string sql = "SELECT * FROM indexed_files WHERE file_path = @path";

            using (var conn = await OpenAsync().ConfigureAwait(false))
            using (var cmd = new SQLiteCommand(sql, conn))
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

        public Task UpsertIndexedFileAsync(IndexedFile file)
        {
            const string sql = @"
                INSERT OR REPLACE INTO indexed_files (file_path, content_hash, last_indexed, is_engine)
                VALUES (@path, @hash, @indexed, @engine)";

            return WriteAsync(async conn =>
            {
                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@path", file.FilePath);
                    cmd.Parameters.AddWithValue("@hash", file.ContentHash);
                    cmd.Parameters.AddWithValue("@indexed", file.LastIndexed.ToString("o"));
                    cmd.Parameters.AddWithValue("@engine", file.IsEngineFile ? 1 : 0);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            });
        }

        // ── Symbol writes (indexing) ────────────────────────────────

        /// <summary>
        /// Batch upsert symbols inside a single transaction for maximum throughput.
        /// Returns the number of symbols written.
        /// </summary>
        public async Task<int> UpsertSymbolsBatchAsync(IReadOnlyList<UnrealSymbol> symbols)
        {
            if (symbols == null || symbols.Count == 0) return 0;

            await WriteAsync(conn =>
            {
                using (var txn = conn.BeginTransaction())
                {
                    const string sql = @"
                        INSERT INTO symbols (name, fully_qualified_name, kind, file_path, line_number, column_number,
                                            owner_class, signature, return_type, comment, is_engine, last_indexed)
                        VALUES (@name, @fqn, @kind, @file, @line, @col, @owner, @sig, @ret, @cmt, @eng, @idx)";

                    using (var cmd = new SQLiteCommand(sql, conn))
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
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            return symbols.Count;
        }

        /// <summary>Batch upsert class_info rows.</summary>
        public Task UpsertClassInfoBatchAsync(IReadOnlyList<UnrealClassInfo> infos)
        {
            if (infos == null || infos.Count == 0) return Task.CompletedTask;

            return WriteAsync(conn =>
            {
                using (var txn = conn.BeginTransaction())
                {
                    const string sql = @"
                        INSERT OR REPLACE INTO class_info (symbol_id, base_class, module, meta_specifiers, is_abstract)
                        VALUES (@sid, @base, @mod, @meta, @abs)";

                    using (var cmd = new SQLiteCommand(sql, conn))
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
                return Task.CompletedTask;
            });
        }

        /// <summary>Removes all symbols that belong to a given source file. Called before re-indexing a file.</summary>
        public Task DeleteSymbolsByFileAsync(string filePath) =>
            ExecuteWriteAsync("DELETE FROM symbols WHERE file_path = @path", filePath);

        /// <summary>Removes the indexed_files entry and occurrences for a deleted file.</summary>
        public Task DeleteIndexedFileAsync(string filePath) =>
            ExecuteWriteAsync(@"DELETE FROM indexed_files WHERE file_path = @path;
                                DELETE FROM occurrences WHERE file_path = @path;
                                DELETE FROM occurrence_files WHERE file_path = @path;", filePath);

        /// <summary>Every file path the cache knows about (symbols, tracked files, occurrences).</summary>
        public async Task<List<string>> GetAllFilePathsAsync()
        {
            var paths = new List<string>();
            const string sql = @"SELECT file_path FROM indexed_files UNION SELECT DISTINCT file_path FROM symbols
                                 UNION SELECT DISTINCT file_path FROM occurrences";
            using (var conn = await OpenAsync().ConfigureAwait(false))
            using (var cmd = new SQLiteCommand(sql, conn))
            using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                while (await reader.ReadAsync().ConfigureAwait(false))
                    paths.Add(reader.GetString(0));
            return paths;
        }

        // ── Occurrences (Find References / Rename) ──────────────────

        /// <summary>The scan key stored by the last occurrence scan of <paramref name="filePath"/>, or null.</summary>
        public async Task<string> GetOccurrenceScanKeyAsync(string filePath)
        {
            using (var conn = await OpenAsync().ConfigureAwait(false))
            using (var cmd = new SQLiteCommand("SELECT scan_key FROM occurrence_files WHERE file_path = @path", conn))
            {
                cmd.Parameters.AddWithValue("@path", filePath);
                return await cmd.ExecuteScalarAsync().ConfigureAwait(false) as string;
            }
        }

        /// <summary>Atomically replaces every occurrence recorded for <paramref name="filePath"/> and stores its scan key.</summary>
        public Task ReplaceOccurrencesForFileAsync(string filePath, IReadOnlyList<SymbolOccurrence> occurrences, string scanKey)
        {
            return WriteAsync(conn =>
            {
                using (var txn = conn.BeginTransaction())
                {
                    using (var del = new SQLiteCommand(
                        "DELETE FROM occurrences WHERE file_path = @path; " +
                        "INSERT OR REPLACE INTO occurrence_files (file_path, scan_key) VALUES (@path, @key);", conn))
                    {
                        del.Transaction = txn;
                        del.Parameters.AddWithValue("@path", filePath);
                        del.Parameters.AddWithValue("@key", scanKey);
                        del.ExecuteNonQuery();
                    }

                    if (occurrences != null && occurrences.Count > 0)
                    {
                        const string sql = @"
                            INSERT INTO occurrences (symbol_name, file_path, line_number, column_number, context)
                            VALUES (@name, @path, @line, @col, @ctx)";
                        using (var cmd = new SQLiteCommand(sql, conn))
                        {
                            cmd.Transaction = txn;
                            var pName = cmd.Parameters.Add("@name", DbType.String);
                            var pPath = cmd.Parameters.Add("@path", DbType.String);
                            var pLine = cmd.Parameters.Add("@line", DbType.Int32);
                            var pCol  = cmd.Parameters.Add("@col",  DbType.Int32);
                            var pCtx  = cmd.Parameters.Add("@ctx",  DbType.Int32);
                            pPath.Value = filePath;

                            foreach (var o in occurrences)
                            {
                                pName.Value = o.SymbolName;
                                pLine.Value = o.LineNumber;
                                pCol.Value  = o.ColumnNumber;
                                pCtx.Value  = (int)o.Context;
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }

                    txn.Commit();
                }
                return Task.CompletedTask;
            });
        }

        /// <summary>All recorded occurrences of an identifier, ordered by file and position.</summary>
        public async Task<List<SymbolOccurrence>> GetOccurrencesAsync(string symbolName, int limit = 5000)
        {
            var results = new List<SymbolOccurrence>();
            const string sql = @"
                SELECT symbol_name, file_path, line_number, column_number, context
                FROM occurrences WHERE symbol_name = @name
                ORDER BY file_path, line_number, column_number LIMIT @limit";

            using (var conn = await OpenAsync().ConfigureAwait(false))
            using (var cmd = new SQLiteCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@name", symbolName);
                cmd.Parameters.AddWithValue("@limit", limit);
                using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        results.Add(new SymbolOccurrence
                        {
                            SymbolName = reader.GetString(0),
                            FilePath = reader.GetString(1),
                            LineNumber = reader.GetInt32(2),
                            ColumnNumber = reader.GetInt32(3),
                            Context = (OccurrenceContext)reader.GetInt32(4)
                        });
                    }
                }
            }

            return results;
        }

        // ── Usage tracking ──────────────────────────────────────────

        public Task IncrementUsageAsync(long symbolId)
        {
            const string sql = @"
                INSERT INTO symbol_usage (symbol_id, access_count, last_accessed)
                VALUES (@id, 1, @now)
                ON CONFLICT(symbol_id) DO UPDATE SET
                    access_count = access_count + 1,
                    last_accessed = @now";

            return WriteAsync(async conn =>
            {
                using (var cmd = new SQLiteCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", symbolId);
                    cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o"));
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            });
        }

        /// <summary>Returns the total number of symbols in the database.</summary>

        /// <summary>Row counts for the Self-Test: indexed files and recorded occurrences.</summary>
        public Task<long> GetIndexedFileCountAsync() => CountAsync("SELECT COUNT(*) FROM indexed_files");
        public Task<long> GetOccurrenceCountAsync() => CountAsync("SELECT COUNT(*) FROM occurrences");

        private async Task<long> CountAsync(string sql)
        {
            using (var conn = await OpenAsync().ConfigureAwait(false))
            using (var cmd = new SQLiteCommand(sql, conn))
            {
                var result = await cmd.ExecuteScalarAsync().ConfigureAwait(false);
                return Convert.ToInt64(result);
            }
        }

        // ── Helpers ─────────────────────────────────────────────────

        private async Task<List<UnrealSymbol>> ReadSymbolsAsync(string sql, Action<SQLiteCommand> bind, int capacity = 16)
        {
            var results = new List<UnrealSymbol>(capacity);
            using (var conn = await OpenAsync().ConfigureAwait(false))
            using (var cmd = new SQLiteCommand(sql, conn))
            {
                bind?.Invoke(cmd);
                using (var reader = (SQLiteDataReader)await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                {
                    var ordinals = new SymbolOrdinals(reader);
                    while (await reader.ReadAsync().ConfigureAwait(false))
                        results.Add(ReadSymbol(reader, ordinals));
                }
            }
            return results;
        }

        /// <summary>Column ordinals resolved once per reader instead of per row.</summary>
        private sealed class SymbolOrdinals
        {
            public readonly int Id, Name, Fqn, Kind, File, Line, Col, Owner, Sig, Ret, Cmt, Eng, Idx, Access;

            public SymbolOrdinals(SQLiteDataReader r)
            {
                Id = r.GetOrdinal("id"); Name = r.GetOrdinal("name"); Fqn = r.GetOrdinal("fully_qualified_name");
                Kind = r.GetOrdinal("kind"); File = r.GetOrdinal("file_path"); Line = r.GetOrdinal("line_number");
                Col = r.GetOrdinal("column_number"); Owner = r.GetOrdinal("owner_class"); Sig = r.GetOrdinal("signature");
                Ret = r.GetOrdinal("return_type"); Cmt = r.GetOrdinal("comment"); Eng = r.GetOrdinal("is_engine");
                Idx = r.GetOrdinal("last_indexed"); Access = r.GetOrdinal("access_count");
            }
        }

        private static UnrealSymbol ReadSymbol(SQLiteDataReader reader, SymbolOrdinals o)
        {
            return new UnrealSymbol
            {
                Id                 = reader.GetInt64(o.Id),
                Name               = reader.GetString(o.Name),
                FullyQualifiedName = reader.IsDBNull(o.Fqn) ? null : reader.GetString(o.Fqn),
                Kind               = (UnrealSymbolKind)reader.GetInt32(o.Kind),
                FilePath           = reader.GetString(o.File),
                LineNumber         = reader.GetInt32(o.Line),
                ColumnNumber       = reader.GetInt32(o.Col),
                OwnerClass         = reader.IsDBNull(o.Owner) ? null : reader.GetString(o.Owner),
                Signature          = reader.IsDBNull(o.Sig) ? null : reader.GetString(o.Sig),
                ReturnType         = reader.IsDBNull(o.Ret) ? null : reader.GetString(o.Ret),
                Comment            = reader.IsDBNull(o.Cmt) ? null : reader.GetString(o.Cmt),
                IsEngineSymbol     = reader.GetInt32(o.Eng) != 0,
                LastIndexed        = DateTime.Parse(reader.GetString(o.Idx)),
                AccessCount        = reader.IsDBNull(o.Access) ? 0 : reader.GetInt32(o.Access)
            };
        }

        private Task ExecuteWriteAsync(string sql, string path) => WriteAsync(async conn =>
        {
            using (var cmd = new SQLiteCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@path", path);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        });

        private static async Task ExecuteNonQueryAsync(SQLiteConnection conn, string sql)
        {
            using (var cmd = new SQLiteCommand(sql, conn))
            {
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        // ── IDisposable ─────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Wait for an in-flight write so its connection isn't torn down mid-transaction.
            if (_writeLock.Wait(TimeSpan.FromSeconds(5)))
                _writeLock.Release();
            if (_connectionString != null)
                SQLiteConnection.ClearAllPools();
        }
    }
}
