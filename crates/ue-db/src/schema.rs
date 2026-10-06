//! Versioned, transactional migrations applied by [`crate::Db::open`].
use crate::{DbError, Result};
use rusqlite::{Connection, TransactionBehavior};

/// SQLite `user_version` supported by this crate. Never downgrade newer DBs.
pub const SCHEMA_VERSION: u32 = 2;

/// Applies pending versions atomically. Failure preserves all pre-migration
/// rows and the old version. Version 0 includes the original unversioned DBs.
pub fn migrate(conn: &mut Connection) -> Result<()> {
    let tx = conn.transaction_with_behavior(TransactionBehavior::Immediate)?;
    let version: u32 = tx.pragma_query_value(None, "user_version", |row| row.get(0))?;
    if version > SCHEMA_VERSION {
        return Err(DbError::NewerSchema {
            found: version,
            supported: SCHEMA_VERSION,
        });
    }
    if version < 1 {
        tx.execute_batch(SCHEMA)?;
    }
    if version < 2 {
        tx.execute_batch(METADATA_SCHEMA)?;
    }
    tx.pragma_update(None, "user_version", SCHEMA_VERSION)?;
    tx.commit()?;
    Ok(())
}

const METADATA_SCHEMA: &str = r#"
ALTER TABLE files ADD COLUMN parser_version INTEGER NOT NULL DEFAULT 0;
ALTER TABLE files ADD COLUMN metadata_version INTEGER NOT NULL DEFAULT 0;
CREATE TABLE symbol_metadata (
    symbol_id INTEGER PRIMARY KEY REFERENCES symbols(id) ON DELETE CASCADE,
    owner TEXT,
    qualified_name TEXT,
    signature TEXT,
    declaration TEXT,
    documentation TEXT,
    name_start INTEGER,
    name_end INTEGER,
    declaration_start INTEGER,
    declaration_end INTEGER,
    CHECK ((name_start IS NULL AND name_end IS NULL) OR
           (name_start IS NOT NULL AND name_end IS NOT NULL AND name_start >= 0 AND name_end >= name_start)),
    CHECK ((declaration_start IS NULL AND declaration_end IS NULL) OR
           (declaration_start IS NOT NULL AND declaration_end IS NOT NULL AND declaration_start >= 0 AND declaration_end >= declaration_start))
);
CREATE INDEX idx_metadata_qualified ON symbol_metadata(qualified_name);
CREATE INDEX idx_metadata_owner ON symbol_metadata(owner);
"#;

/// The original version-1 schema. Every statement is `IF NOT EXISTS`, so running it against an
/// existing database is a no-op and it doubles as the migration for version 1.
///
/// `bases` is a table rather than a JSON column so inheritance queries stay
/// plain SQL — see [`crate::Db::derived_from`].
pub const SCHEMA: &str = r#"
CREATE TABLE IF NOT EXISTS files (
    id    INTEGER PRIMARY KEY,
    path  TEXT NOT NULL UNIQUE,
    mtime INTEGER NOT NULL,
    hash  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS symbols (
    id         INTEGER PRIMARY KEY,
    file_id    INTEGER NOT NULL REFERENCES files(id) ON DELETE CASCADE,
    name       TEXT NOT NULL,
    kind       TEXT NOT NULL,
    macro_name TEXT NOT NULL,
    type_name  TEXT,
    line       INTEGER NOT NULL,
    byte_start INTEGER NOT NULL,
    byte_end   INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS specifiers (
    id        INTEGER PRIMARY KEY,
    symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    ordinal   INTEGER NOT NULL,
    key       TEXT NOT NULL,
    value     TEXT
);

CREATE TABLE IF NOT EXISTS bases (
    symbol_id INTEGER NOT NULL REFERENCES symbols(id) ON DELETE CASCADE,
    ordinal   INTEGER NOT NULL,
    name      TEXT NOT NULL,
    PRIMARY KEY (symbol_id, ordinal)
);

CREATE INDEX IF NOT EXISTS idx_symbols_name       ON symbols(name);
CREATE INDEX IF NOT EXISTS idx_symbols_kind       ON symbols(kind);
CREATE INDEX IF NOT EXISTS idx_symbols_file       ON symbols(file_id);
CREATE INDEX IF NOT EXISTS idx_specifiers_symbol  ON specifiers(symbol_id);
CREATE INDEX IF NOT EXISTS idx_bases_name         ON bases(name);
"#;
