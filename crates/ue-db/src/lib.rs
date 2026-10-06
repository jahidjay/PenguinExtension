//! Persistent symbol store for the Penguin indexer.
//!
//! Everything goes through a [`r2d2`] connection pool in WAL mode, so readers
//! never block the writer and a [`Db`] handle can be cloned freely across
//! threads and async tasks. That is the structural fix for the legacy
//! extension's single shared `SQLiteConnection`, which serialized every query
//! and could be disposed out from under an in-flight index pass.
//!
//! ```no_run
//! # fn main() -> Result<(), ue_db::DbError> {
//! let db = ue_db::Db::open("penguin.db")?;
//! for hit in db.find("AMyActor")? {
//!     println!("{} at {}:{}", hit.symbol.name, hit.file, hit.symbol.line);
//! }
//! # Ok(())
//! # }
//! ```

pub mod model;
pub mod query;
pub mod schema;

use std::fmt;
use std::path::Path;
use std::time::Duration;

use r2d2::{CustomizeConnection, Pool};
use r2d2_sqlite::SqliteConnectionManager;
use rusqlite::Connection;
use ue_parser::{ParsedFileWithMetadata, SymbolMetadata, UnrealSymbol, METADATA_VERSION};

pub use model::{content_hash, path_key, FileEntry, StoredSymbol, StoredSymbolWithMetadata};

/// Anything that can go wrong talking to the store.
#[derive(Debug)]
pub enum DbError {
    /// No connection could be checked out of the pool.
    Pool(r2d2::Error),
    /// SQLite rejected a statement.
    Sqlite(rusqlite::Error),
    /// Refuse to rewrite or delete a database created by a newer release.
    NewerSchema { found: u32, supported: u32 },
    /// The rich DTO is not aligned with its symbols or has invalid byte ranges.
    InvalidMetadata(&'static str),
}

impl fmt::Display for DbError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            DbError::Pool(e) => write!(f, "could not acquire a database connection: {e}"),
            DbError::Sqlite(e) => write!(f, "database error: {e}"),
            DbError::NewerSchema { found, supported } => write!(
                f,
                "database schema {found} is newer than supported version {supported}"
            ),
            DbError::InvalidMetadata(message) => write!(f, "invalid symbol metadata: {message}"),
        }
    }
}

impl std::error::Error for DbError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            DbError::Pool(e) => Some(e),
            DbError::Sqlite(e) => Some(e),
            DbError::NewerSchema { .. } | DbError::InvalidMetadata(_) => None,
        }
    }
}

impl From<r2d2::Error> for DbError {
    fn from(e: r2d2::Error) -> Self {
        DbError::Pool(e)
    }
}

impl From<rusqlite::Error> for DbError {
    fn from(e: rusqlite::Error) -> Self {
        DbError::Sqlite(e)
    }
}

pub type Result<T> = std::result::Result<T, DbError>;

/// Applies the per-connection pragmas the store depends on.
///
/// These are connection-scoped in SQLite, so setting them once at open time
/// would silently leave every other pooled connection on the defaults.
#[derive(Debug)]
struct WalCustomizer;

impl CustomizeConnection<Connection, rusqlite::Error> for WalCustomizer {
    fn on_acquire(&self, conn: &mut Connection) -> std::result::Result<(), rusqlite::Error> {
        // `PRAGMA journal_mode` answers with the mode it settled on, so it has
        // to be run as a query rather than an update.
        let _: String = conn.query_row("PRAGMA journal_mode=WAL", [], |row| row.get(0))?;
        conn.pragma_update(None, "synchronous", "NORMAL")?;
        conn.pragma_update(None, "foreign_keys", true)?;
        conn.busy_timeout(Duration::from_secs(5))?;
        Ok(())
    }
}

/// A handle to the symbol store.
///
/// Cloning is cheap and shares the underlying pool, which is how the LSP hands
/// the same database to concurrent request handlers and indexing workers.
#[derive(Clone)]
pub struct Db {
    pool: Pool<SqliteConnectionManager>,
}

impl fmt::Debug for Db {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("Db")
            .field("connections", &self.pool.state().connections)
            .finish()
    }
}

impl Db {
    /// Opens (creating if needed) a WAL database and migrates it transactionally.
    /// A newer schema returns [`DbError::NewerSchema`]; migration errors are
    /// propagated without deleting, clearing, or recreating the existing cache.
    pub fn open(path: impl AsRef<Path>) -> Result<Self> {
        let manager = SqliteConnectionManager::file(path.as_ref());
        let pool = Pool::builder()
            .connection_customizer(Box::new(WalCustomizer))
            .build(manager)?;
        let db = Db { pool };
        db.migrate()?;
        Ok(db)
    }

    fn migrate(&self) -> Result<()> {
        let mut conn = self.pool.get()?;
        schema::migrate(&mut conn)
    }

    pub(crate) fn conn(&self) -> Result<r2d2::PooledConnection<SqliteConnectionManager>> {
        Ok(self.pool.get()?)
    }

    /// Whether `path` has to be parsed again.
    ///
    /// Compares mtime, content hash, and parser/metadata extraction generation.
    /// Migrated rows start at version zero and reindex even if unchanged.
    /// Symbol-only writes satisfy this legacy check; rich callers should use
    /// [`Db::needs_metadata_reindex`] to backfill those rows.
    pub fn needs_reindex(&self, path: &Path, mtime: i64, hash: &str) -> Result<bool> {
        let conn = self.conn()?;
        let found: Option<(i64, String, u32)> = conn
            .query_row(
                "SELECT mtime, hash, parser_version FROM files WHERE path = ?1",
                [path_key(path)],
                |row| Ok((row.get(0)?, row.get(1)?, row.get(2)?)),
            )
            .optional_row()?;
        Ok(match found {
            None => true,
            Some((stored_mtime, stored_hash, version)) => {
                stored_mtime != mtime || stored_hash != hash || version != METADATA_VERSION
            }
        })
    }

    /// Like [`Db::needs_reindex`], but also true for symbol-only cached files.
    /// Pair this with [`Db::replace_file_with_metadata`] for rich indexing.
    /// An all-unknown metadata vector still counts as processed.
    pub fn needs_metadata_reindex(&self, path: &Path, mtime: i64, hash: &str) -> Result<bool> {
        let conn = self.conn()?;
        let current: Option<bool> = conn.query_row(
            "SELECT mtime = ?2 AND hash = ?3 AND parser_version = ?4 AND metadata_version = ?4 FROM files WHERE path = ?1",
            rusqlite::params![path_key(path), mtime, hash, METADATA_VERSION],
            |row| row.get(0),
        ).optional_row()?;
        Ok(current != Some(true))
    }

    /// Writes one file's symbols, replacing whatever was stored for it before.
    ///
    /// The delete-then-insert runs inside a single transaction, so a re-index
    /// can never leave a half-updated file behind or strand symbols that the
    /// header no longer declares. Marks the current parser generation but not
    /// rich metadata completion; use [`Db::replace_file_with_metadata`] for that.
    /// Existing rich rows are removed with their old symbols, never reused.
    pub fn replace_file(&self, file: &FileEntry, symbols: &[UnrealSymbol]) -> Result<i64> {
        self.replace(file, symbols, None)
    }

    /// Stores symbols and parallel metadata atomically, keyed by inserted row,
    /// not name: duplicate names and overloads remain distinct. Only pass fresh
    /// results from the current parser. Validates alignment/ranges before any
    /// writes. Does not read or parse source files in the database layer.
    pub fn replace_file_with_metadata(
        &self,
        file: &FileEntry,
        parsed: &ParsedFileWithMetadata,
    ) -> Result<i64> {
        if parsed.symbols.len() != parsed.metadata.len() {
            return Err(DbError::InvalidMetadata(
                "symbols and metadata lengths differ",
            ));
        }
        for metadata in &parsed.metadata {
            for range in [&metadata.name_range, &metadata.declaration_range]
                .into_iter()
                .flatten()
            {
                if range.start > range.end || range.end > i64::MAX as usize {
                    return Err(DbError::InvalidMetadata("invalid byte range"));
                }
            }
        }
        self.replace(file, &parsed.symbols, Some(&parsed.metadata))
    }

    fn replace(
        &self,
        file: &FileEntry,
        symbols: &[UnrealSymbol],
        metadata: Option<&[SymbolMetadata]>,
    ) -> Result<i64> {
        let mut conn = self.conn()?;
        let tx = conn.transaction()?;

        let file_id: i64 = tx.query_row(
            "INSERT INTO files (path, mtime, hash, parser_version, metadata_version) VALUES (?1, ?2, ?3, ?4, ?5)
             ON CONFLICT(path) DO UPDATE SET mtime = excluded.mtime, hash = excluded.hash,
                 parser_version = excluded.parser_version, metadata_version = excluded.metadata_version
             RETURNING id",
            rusqlite::params![file.path, file.mtime, file.hash, METADATA_VERSION, if metadata.is_some() { METADATA_VERSION } else { 0 }],
            |row| row.get(0),
        )?;

        // Cascades through specifiers and bases.
        tx.execute("DELETE FROM symbols WHERE file_id = ?1", [file_id])?;

        {
            let mut insert_symbol = tx.prepare(
                "INSERT INTO symbols
                     (file_id, name, kind, macro_name, type_name, line, byte_start, byte_end)
                 VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)
                 RETURNING id",
            )?;
            let mut insert_specifier = tx.prepare(
                "INSERT INTO specifiers (symbol_id, ordinal, key, value) VALUES (?1, ?2, ?3, ?4)",
            )?;
            let mut insert_base =
                tx.prepare("INSERT INTO bases (symbol_id, ordinal, name) VALUES (?1, ?2, ?3)")?;

            let mut insert_metadata = tx.prepare(
                "INSERT INTO symbol_metadata (symbol_id, owner, qualified_name, signature, declaration, documentation,
                 name_start, name_end, declaration_start, declaration_end) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)"
            )?;
            for (slot, symbol) in symbols.iter().enumerate() {
                let symbol_id: i64 = insert_symbol.query_row(
                    rusqlite::params![
                        file_id,
                        symbol.name,
                        symbol.kind.as_str(),
                        symbol.macro_name,
                        symbol.type_name,
                        symbol.line as i64,
                        symbol.byte_range.start as i64,
                        symbol.byte_range.end as i64,
                    ],
                    |row| row.get(0),
                )?;

                if let Some(metadata) = metadata {
                    let m = &metadata[slot];
                    insert_metadata.execute(rusqlite::params![
                        symbol_id,
                        m.owner,
                        m.qualified_name,
                        m.signature,
                        m.declaration,
                        m.documentation,
                        m.name_range.as_ref().map(|r| r.start as i64),
                        m.name_range.as_ref().map(|r| r.end as i64),
                        m.declaration_range.as_ref().map(|r| r.start as i64),
                        m.declaration_range.as_ref().map(|r| r.end as i64)
                    ])?;
                }

                for (ordinal, spec) in symbol.specifiers.iter().enumerate() {
                    insert_specifier.execute(rusqlite::params![
                        symbol_id,
                        ordinal as i64,
                        spec.key,
                        spec.value,
                    ])?;
                }
                for (ordinal, base) in symbol.bases.iter().enumerate() {
                    insert_base.execute(rusqlite::params![symbol_id, ordinal as i64, base])?;
                }
            }
        }

        tx.commit()?;
        Ok(file_id)
    }

    /// Drops a file and everything declared in it. Returns whether a row went.
    pub fn remove_file(&self, path: &Path) -> Result<bool> {
        let conn = self.conn()?;
        let removed = conn.execute("DELETE FROM files WHERE path = ?1", [path_key(path)])?;
        Ok(removed > 0)
    }

    /// Empties the store, keeping the schema.
    pub fn clear(&self) -> Result<()> {
        let conn = self.conn()?;
        conn.execute_batch("DELETE FROM files;")?;
        Ok(())
    }
}

/// `query_row` returns `QueryReturnedNoRows` rather than `None`; this turns that
/// one error back into an absence without swallowing real failures.
trait OptionalRow<T> {
    fn optional_row(self) -> rusqlite::Result<Option<T>>;
}

impl<T> OptionalRow<T> for rusqlite::Result<T> {
    fn optional_row(self) -> rusqlite::Result<Option<T>> {
        match self {
            Ok(value) => Ok(Some(value)),
            Err(rusqlite::Error::QueryReturnedNoRows) => Ok(None),
            Err(e) => Err(e),
        }
    }
}
