//! Read side of the store.
//!
//! Every lookup returns fully hydrated [`StoredSymbol`]s: the specifiers and
//! base classes for a whole result set are fetched in one extra query each,
//! rather than one per symbol, so a prefix completion over a large project
//! stays at three round trips regardless of how many symbols match.

use std::collections::{HashMap, HashSet};
use std::path::Path;

use rusqlite::{Connection, Row};
use ue_parser::{Specifier, SymbolKind, SymbolMetadata, UnrealSymbol, METADATA_VERSION};

use crate::model::{path_key, StoredSymbol, StoredSymbolWithMetadata};
use crate::{Db, Result};

/// How deep [`Db::inheritance_chain`] will walk before giving up.
///
/// Real Unreal hierarchies are nowhere near this deep; the cap is a backstop
/// for a malformed index, on top of the visited set that handles true cycles.
const MAX_CHAIN: usize = 64;

/// Columns every symbol query selects, in the order [`read_symbol_row`] expects.
const SELECT_SYMBOL: &str = "SELECT s.id, f.path, s.name, s.kind, s.macro_name, \
                             s.type_name, s.line, s.byte_start, s.byte_end \
                             FROM symbols s JOIN files f ON f.id = s.file_id";

/// Counts for a status line or a sanity check after indexing.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub struct Stats {
    pub files: usize,
    pub symbols: usize,
}

impl Db {
    /// Every symbol with this exact name. Unreal allows the same name in several
    /// modules, so this is a list rather than an option.
    pub fn find(&self, name: &str) -> Result<Vec<StoredSymbol>> {
        let conn = self.conn()?;
        let sql = format!("{SELECT_SYMBOL} WHERE s.name = ?1 ORDER BY f.path, s.line");
        collect(&conn, &sql, rusqlite::params![name])
    }

    /// Exact short-name matches including optional metadata. Duplicates and
    /// overloads are separate rows, ordered by file and macro byte offset.
    /// Metadata from an older extraction generation is returned as absent.
    pub fn find_with_metadata(&self, name: &str) -> Result<Vec<StoredSymbolWithMetadata>> {
        self.rich_query("s.name = ?1", rusqlite::params![name])
    }

    /// Rich symbols from one file in macro source order. Existing macro ranges
    /// are unchanged; navigation may prefer `metadata.name_range` when present.
    pub fn in_file_with_metadata(&self, path: &Path) -> Result<Vec<StoredSymbolWithMetadata>> {
        self.rich_query("f.path = ?1", rusqlite::params![path_key(path)])
    }

    /// Matches a syntax-qualified name, not a resolved C++ identity. Overloads
    /// are not collapsed: callers can distinguish by signature and macro range.
    /// Returns only current, known metadata, never guesses from short names.
    pub fn find_qualified(&self, name: &str) -> Result<Vec<StoredSymbolWithMetadata>> {
        self.rich_query("m.qualified_name = ?1", rusqlite::params![name])
    }

    /// Symbols whose syntax-established owner exactly matches `owner`. No
    /// inheritance, namespace alias resolution, or filename heuristics are used.
    pub fn members_of(&self, owner: &str) -> Result<Vec<StoredSymbolWithMetadata>> {
        self.rich_query("m.owner = ?1", rusqlite::params![owner])
    }

    fn rich_query(
        &self,
        predicate: &str,
        params: &[&dyn rusqlite::ToSql],
    ) -> Result<Vec<StoredSymbolWithMetadata>> {
        let mut conn = self.conn()?;
        // Keep hydration consistent with the selected rows if a writer replaces
        // the file between queries (symbol row ids may be reused by SQLite).
        let tx = conn.transaction()?;
        let sql = format!(
            "{SELECT_SYMBOL} LEFT JOIN symbol_metadata m ON m.symbol_id = s.id
            AND f.metadata_version = {METADATA_VERSION} AND f.parser_version = {METADATA_VERSION}
            WHERE {predicate} ORDER BY f.path, s.byte_start, s.id"
        );
        let (ids, symbols) = collect_with_ids(&tx, &sql, params)?;
        let mut out: Vec<_> = symbols
            .into_iter()
            .map(|s| StoredSymbolWithMetadata {
                file: s.file,
                symbol: s.symbol,
                metadata: None,
            })
            .collect();
        if let Some(sql) = in_clause(
            "SELECT m.symbol_id, m.owner, m.qualified_name, m.signature, m.declaration, m.documentation,
             m.name_start, m.name_end, m.declaration_start, m.declaration_end
             FROM symbol_metadata m JOIN symbols s ON s.id = m.symbol_id JOIN files f ON f.id = s.file_id",
            &ids,
            &format!("AND f.metadata_version = {METADATA_VERSION} AND f.parser_version = {METADATA_VERSION}"),
        ) {
            let index = index_of(&ids);
            let mut stmt = tx.prepare(&sql)?;
            let rows = stmt.query_map([], |row| {
                Ok((row.get::<_, i64>(0)?, SymbolMetadata {
                    owner: row.get(1)?, qualified_name: row.get(2)?, signature: row.get(3)?,
                    declaration: row.get(4)?, documentation: row.get(5)?,
                    name_range: read_range(row, 6)?, declaration_range: read_range(row, 8)?,
                }))
            })?;
            for row in rows {
                let (id, metadata) = row?;
                if let Some(&slot) = index.get(&id) { out[slot].metadata = Some(metadata); }
            }
        }
        tx.commit()?;
        Ok(out)
    }

    /// Symbols whose name starts with `prefix`, case-insensitively — the query
    /// behind completion. `limit` caps the result set.
    pub fn prefix(&self, prefix: &str, limit: usize) -> Result<Vec<StoredSymbol>> {
        let conn = self.conn()?;
        let sql = format!(
            "{SELECT_SYMBOL} WHERE s.name LIKE ?1 ESCAPE '\\' ORDER BY length(s.name), s.name LIMIT ?2"
        );
        collect(
            &conn,
            &sql,
            rusqlite::params![like_prefix(prefix), limit as i64],
        )
    }

    /// Everything declared in one header, in source order.
    pub fn in_file(&self, path: &Path) -> Result<Vec<StoredSymbol>> {
        let conn = self.conn()?;
        let sql = format!("{SELECT_SYMBOL} WHERE f.path = ?1 ORDER BY s.line, s.byte_start");
        collect(&conn, &sql, rusqlite::params![path_key(path)])
    }

    /// Every symbol of one kind, newest-indexed last. `limit` caps the result.
    pub fn of_kind(&self, kind: SymbolKind, limit: usize) -> Result<Vec<StoredSymbol>> {
        let conn = self.conn()?;
        let sql = format!("{SELECT_SYMBOL} WHERE s.kind = ?1 ORDER BY s.name LIMIT ?2");
        collect(&conn, &sql, rusqlite::params![kind.as_str(), limit as i64])
    }

    /// Fuzzy-ish workspace search: any symbol containing `query`.
    pub fn search(&self, query: &str, limit: usize) -> Result<Vec<StoredSymbol>> {
        let conn = self.conn()?;
        let sql = format!(
            "{SELECT_SYMBOL} WHERE s.name LIKE ?1 ESCAPE '\\' ORDER BY length(s.name), s.name LIMIT ?2"
        );
        let pattern = format!("%{}%", escape_like(query));
        collect(&conn, &sql, rusqlite::params![pattern, limit as i64])
    }

    /// Ancestors of `name`, nearest first, excluding `name` itself.
    ///
    /// Breadth-first so a class with several bases reports its direct parents
    /// before their parents. A visited set plus a 64-level cap keeps a cyclic or
    /// corrupted index from spinning — the legacy extension had no such guard.
    pub fn inheritance_chain(&self, name: &str) -> Result<Vec<String>> {
        let conn = self.conn()?;
        let mut stmt = conn.prepare(
            "SELECT b.name FROM bases b JOIN symbols s ON s.id = b.symbol_id
             WHERE s.name = ?1 ORDER BY b.ordinal",
        )?;

        let mut seen: HashSet<String> = HashSet::new();
        seen.insert(name.to_string());

        let mut chain = Vec::new();
        let mut frontier = vec![name.to_string()];

        for _ in 0..MAX_CHAIN {
            if frontier.is_empty() {
                break;
            }
            let mut next = Vec::new();
            for current in &frontier {
                let rows = stmt.query_map([current], |row| row.get::<_, String>(0))?;
                for base in rows {
                    let base = base?;
                    if seen.insert(base.clone()) {
                        chain.push(base.clone());
                        next.push(base);
                    }
                }
            }
            frontier = next;
        }

        Ok(chain)
    }

    /// Symbols that name `base` as a direct base class.
    pub fn derived_from(&self, base: &str) -> Result<Vec<StoredSymbol>> {
        let conn = self.conn()?;
        let sql = format!(
            "{SELECT_SYMBOL} JOIN bases b ON b.symbol_id = s.id \
             WHERE b.name = ?1 ORDER BY s.name"
        );
        collect(&conn, &sql, rusqlite::params![base])
    }

    /// Normalized paths of every indexed header.
    pub fn files(&self) -> Result<Vec<String>> {
        let conn = self.conn()?;
        let mut stmt = conn.prepare("SELECT path FROM files ORDER BY path")?;
        let rows = stmt.query_map([], |row| row.get::<_, String>(0))?;
        let mut out = Vec::new();
        for row in rows {
            out.push(row?);
        }
        Ok(out)
    }

    /// How much is in the store.
    pub fn stats(&self) -> Result<Stats> {
        let conn = self.conn()?;
        let files: i64 = conn.query_row("SELECT COUNT(*) FROM files", [], |row| row.get(0))?;
        let symbols: i64 = conn.query_row("SELECT COUNT(*) FROM symbols", [], |row| row.get(0))?;
        Ok(Stats {
            files: files as usize,
            symbols: symbols as usize,
        })
    }
}

/// Runs a symbol query and attaches specifiers and bases to the results.
fn collect(
    conn: &Connection,
    sql: &str,
    params: &[&dyn rusqlite::ToSql],
) -> Result<Vec<StoredSymbol>> {
    Ok(collect_with_ids(conn, sql, params)?.1)
}

fn collect_with_ids(
    conn: &Connection,
    sql: &str,
    params: &[&dyn rusqlite::ToSql],
) -> Result<(Vec<i64>, Vec<StoredSymbol>)> {
    let mut stmt = conn.prepare(sql)?;
    let rows = stmt.query_map(params, read_symbol_row)?;

    let mut ids = Vec::new();
    let mut symbols = Vec::new();
    for row in rows {
        // `None` means the `kind` column held something no longer in the enum,
        // which can only happen if the file was edited outside this crate.
        if let Some((id, symbol)) = row? {
            ids.push(id);
            symbols.push(symbol);
        }
    }

    attach_specifiers(conn, &ids, &mut symbols)?;
    attach_bases(conn, &ids, &mut symbols)?;
    Ok((ids, symbols))
}

fn read_range(row: &Row<'_>, column: usize) -> rusqlite::Result<Option<std::ops::Range<usize>>> {
    let start: Option<i64> = row.get(column)?;
    let end: Option<i64> = row.get(column + 1)?;
    Ok(match (start, end) {
        (Some(start), Some(end)) if start >= 0 && end >= start => {
            Some(start as usize..end as usize)
        }
        _ => None,
    })
}

fn read_symbol_row(row: &Row<'_>) -> rusqlite::Result<Option<(i64, StoredSymbol)>> {
    let kind_text: String = row.get(3)?;
    let Some(kind) = SymbolKind::from_name(&kind_text) else {
        return Ok(None);
    };

    let id: i64 = row.get(0)?;
    let file: String = row.get(1)?;
    let line: i64 = row.get(6)?;
    let start: i64 = row.get(7)?;
    let end: i64 = row.get(8)?;

    let symbol = UnrealSymbol {
        name: row.get(2)?,
        kind,
        macro_name: row.get(4)?,
        specifiers: Vec::new(),
        type_name: row.get(5)?,
        bases: Vec::new(),
        line: line.max(0) as usize,
        byte_range: (start.max(0) as usize)..(end.max(0) as usize),
    };

    Ok(Some((id, StoredSymbol { file, symbol })))
}

fn attach_specifiers(conn: &Connection, ids: &[i64], symbols: &mut [StoredSymbol]) -> Result<()> {
    let Some(sql) = in_clause(
        "SELECT symbol_id, key, value FROM specifiers",
        ids,
        "ORDER BY symbol_id, ordinal",
    ) else {
        return Ok(());
    };

    let index = index_of(ids);
    let mut stmt = conn.prepare(&sql)?;
    let rows = stmt.query_map([], |row| {
        Ok((
            row.get::<_, i64>(0)?,
            row.get::<_, String>(1)?,
            row.get::<_, Option<String>>(2)?,
        ))
    })?;

    for row in rows {
        let (symbol_id, key, value) = row?;
        if let Some(&slot) = index.get(&symbol_id) {
            symbols[slot]
                .symbol
                .specifiers
                .push(Specifier { key, value });
        }
    }
    Ok(())
}

fn attach_bases(conn: &Connection, ids: &[i64], symbols: &mut [StoredSymbol]) -> Result<()> {
    let Some(sql) = in_clause(
        "SELECT symbol_id, name FROM bases",
        ids,
        "ORDER BY symbol_id, ordinal",
    ) else {
        return Ok(());
    };

    let index = index_of(ids);
    let mut stmt = conn.prepare(&sql)?;
    let rows = stmt.query_map([], |row| {
        Ok((row.get::<_, i64>(0)?, row.get::<_, String>(1)?))
    })?;

    for row in rows {
        let (symbol_id, name) = row?;
        if let Some(&slot) = index.get(&symbol_id) {
            symbols[slot].symbol.bases.push(name);
        }
    }
    Ok(())
}

/// Builds `<head> WHERE symbol_id IN (…) <tail>`, or `None` when there is
/// nothing to look up — SQLite rejects an empty `IN ()`.
///
/// The ids are `i64`s that came out of the database a moment ago, so splicing
/// them into the SQL is safe and avoids rebuilding the statement per batch size.
fn in_clause(head: &str, ids: &[i64], tail: &str) -> Option<String> {
    if ids.is_empty() {
        return None;
    }
    let list = ids
        .iter()
        .map(|id| id.to_string())
        .collect::<Vec<_>>()
        .join(",");
    Some(format!("{head} WHERE symbol_id IN ({list}) {tail}"))
}

/// Maps a symbol id back to its position in the result vector.
fn index_of(ids: &[i64]) -> HashMap<i64, usize> {
    ids.iter().enumerate().map(|(i, id)| (*id, i)).collect()
}

/// Escapes the LIKE wildcards so a prefix containing `_` matches literally.
fn escape_like(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    for ch in text.chars() {
        if matches!(ch, '%' | '_' | '\\') {
            out.push('\\');
        }
        out.push(ch);
    }
    out
}

/// A LIKE pattern matching anything starting with `prefix`.
///
/// SQLite's LIKE is ASCII-case-insensitive by default, which is exactly what
/// completion wants from a half-typed identifier.
fn like_prefix(prefix: &str) -> String {
    format!("{}%", escape_like(prefix))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn wildcards_in_a_prefix_are_literal() {
        assert_eq!(like_prefix("On_"), r"On\_%");
        assert_eq!(like_prefix("100%"), r"100\%%");
        assert_eq!(like_prefix("AMy"), "AMy%");
    }

    #[test]
    fn empty_id_list_produces_no_statement() {
        assert!(in_clause("SELECT 1", &[], "").is_none());
        assert_eq!(
            in_clause("SELECT 1", &[3, 7], "ORDER BY x").unwrap(),
            "SELECT 1 WHERE symbol_id IN (3,7) ORDER BY x"
        );
    }
}
