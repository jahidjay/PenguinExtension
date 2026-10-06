//! Rich reads, non-destructive migrations and parser-generation invalidation.
use rusqlite::Connection;
use std::path::Path;
use ue_db::{Db, DbError, FileEntry};
use ue_parser::{UnrealParser, METADATA_VERSION};

const SOURCE: &str = r#"
namespace Game {
UCLASS() class AFirst {
    /// Int overload.
    UFUNCTION() void Fire(int Amount);
    /** Float overload. */
    UFUNCTION() void Fire(float Amount) const;
    UPROPERTY() int Health;
};
UCLASS() class ASecond { UPROPERTY() float Health; };
}
"#;

fn parsed(source: &str) -> ue_parser::ParsedFileWithMetadata {
    UnrealParser::new().unwrap().parse_with_metadata(source)
}

#[test]
fn rich_rows_preserve_overloads_names_docs_and_ranges() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("symbols.db");
    let db = Db::open(&path).unwrap();
    let file = FileEntry::new(Path::new("/Game/Actor.h"), 1, SOURCE);
    let parsed = parsed(SOURCE);
    db.replace_file_with_metadata(&file, &parsed).unwrap();
    let rows = db.in_file_with_metadata(Path::new(&file.path)).unwrap();
    assert_eq!(rows.len(), parsed.symbols.len());
    for ((row, symbol), metadata) in rows.iter().zip(&parsed.symbols).zip(&parsed.metadata) {
        assert_eq!(&row.symbol, symbol);
        assert_eq!(row.metadata.as_ref(), Some(metadata));
    }
    let fires = db.find_with_metadata("Fire").unwrap();
    assert_eq!(fires.len(), 2);
    assert_eq!(
        fires[0].metadata.as_ref().unwrap().signature.as_deref(),
        Some("void Fire(int Amount)")
    );
    assert_eq!(
        fires[1].metadata.as_ref().unwrap().documentation.as_deref(),
        Some("/** Float overload. */")
    );
    assert_ne!(fires[0].symbol.byte_range, fires[1].symbol.byte_range);
    assert_eq!(db.find_qualified("Game::AFirst::Fire").unwrap(), fires);
    let health = db.find_with_metadata("Health").unwrap();
    assert_eq!(
        health[0].metadata.as_ref().unwrap().owner.as_deref(),
        Some("Game::AFirst")
    );
    assert_eq!(
        health[1].metadata.as_ref().unwrap().owner.as_deref(),
        Some("Game::ASecond")
    );
    assert_eq!(db.members_of("Game::AFirst").unwrap().len(), 3);
    assert_eq!(
        db.members_of("AFirst").unwrap().len(),
        0,
        "no short-name guessing"
    );
    assert!(!db
        .needs_metadata_reindex(Path::new(&file.path), 1, &file.hash)
        .unwrap());
    drop(db);
    let reopened = Db::open(&path).unwrap();
    assert_eq!(reopened.find_with_metadata("Fire").unwrap(), fires);
    // Another header with identical names stays separate too.
    let other = FileEntry::new(Path::new("/Other/Actor.h"), 1, SOURCE);
    reopened
        .replace_file_with_metadata(&other, &parsed)
        .unwrap();
    assert_eq!(
        reopened.find_qualified("Game::AFirst::Fire").unwrap().len(),
        4
    );
    assert!(reopened.remove_file(Path::new(&other.path)).unwrap());
    let raw = Connection::open(&path).unwrap();
    assert_eq!(
        raw.query_row("SELECT count(*) FROM symbol_metadata", [], |r| r
            .get::<_, usize>(0))
            .unwrap(),
        parsed.symbols.len()
    );
}

fn legacy_database(path: &Path, version: u32) -> FileEntry {
    let conn = Connection::open(path).unwrap();
    conn.execute_batch(ue_db::schema::SCHEMA).unwrap();
    conn.pragma_update(None, "user_version", version).unwrap();
    let entry = FileEntry::new(Path::new("/Game/Legacy.h"), 9, "UPROPERTY() int Value;");
    conn.execute(
        "INSERT INTO files(id,path,mtime,hash) VALUES(1,?1,?2,?3)",
        rusqlite::params![entry.path, entry.mtime, entry.hash],
    )
    .unwrap();
    conn.execute_batch("INSERT INTO symbols(id,file_id,name,kind,macro_name,line,byte_start,byte_end) VALUES(1,1,'Value','property','UPROPERTY',1,0,11);").unwrap();
    entry
}

#[test]
fn unversioned_and_v1_migrations_preserve_cache_until_backfilled() {
    for version in [0, 1] {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("old.db");
        let file = legacy_database(&path, version);
        let db = Db::open(&path).unwrap();
        assert_eq!(db.find("Value").unwrap().len(), 1);
        assert_eq!(db.find_with_metadata("Value").unwrap()[0].metadata, None);
        assert!(db
            .needs_reindex(Path::new(&file.path), file.mtime, &file.hash)
            .unwrap());
        assert!(db
            .needs_metadata_reindex(Path::new(&file.path), file.mtime, &file.hash)
            .unwrap());
        db.replace_file_with_metadata(&file, &parsed("UPROPERTY() int Value;"))
            .unwrap();
        assert!(!db
            .needs_reindex(Path::new(&file.path), file.mtime, &file.hash)
            .unwrap());
        assert!(!db
            .needs_metadata_reindex(Path::new(&file.path), file.mtime, &file.hash)
            .unwrap());
        assert!(db.find_with_metadata("Value").unwrap()[0]
            .metadata
            .is_some());
        let raw = Connection::open(&path).unwrap();
        assert_eq!(
            raw.pragma_query_value(None, "user_version", |r| r.get::<_, u32>(0))
                .unwrap(),
            ue_db::schema::SCHEMA_VERSION
        );
        // Opening an already migrated DB is idempotent.
        assert_eq!(Db::open(&path).unwrap().stats().unwrap().symbols, 1);
    }
}

#[test]
fn failed_migration_rolls_back_ddl_version_and_keeps_existing_rows() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("broken.db");
    legacy_database(&path, 1);
    let raw = Connection::open(&path).unwrap();
    raw.execute_batch("CREATE TABLE symbol_metadata(sentinel TEXT); INSERT INTO symbol_metadata VALUES ('keep me');").unwrap();
    assert!(matches!(Db::open(&path), Err(DbError::Sqlite(_))));
    assert_eq!(
        raw.pragma_query_value(None, "user_version", |r| r.get::<_, u32>(0))
            .unwrap(),
        1
    );
    assert_eq!(
        raw.query_row("SELECT name FROM symbols", [], |r| r.get::<_, String>(0))
            .unwrap(),
        "Value"
    );
    assert_eq!(
        raw.query_row("SELECT sentinel FROM symbol_metadata", [], |r| r
            .get::<_, String>(0))
            .unwrap(),
        "keep me"
    );
    let columns: Vec<String> = raw
        .prepare("PRAGMA table_info(files)")
        .unwrap()
        .query_map([], |r| r.get(1))
        .unwrap()
        .map(Result::unwrap)
        .collect();
    assert!(
        !columns.contains(&"parser_version".to_string()),
        "first ALTER was rolled back"
    );
    assert!(!columns.contains(&"metadata_version".to_string()));
}

#[test]
fn newer_schema_is_rejected_without_downgrading_or_deleting() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("future.db");
    legacy_database(&path, ue_db::schema::SCHEMA_VERSION + 1);
    assert!(matches!(Db::open(&path), Err(DbError::NewerSchema { .. })));
    let raw = Connection::open(&path).unwrap();
    assert_eq!(
        raw.pragma_query_value(None, "user_version", |r| r.get::<_, u32>(0))
            .unwrap(),
        ue_db::schema::SCHEMA_VERSION + 1
    );
    assert_eq!(
        raw.query_row("SELECT count(*) FROM symbols", [], |r| r.get::<_, i64>(0))
            .unwrap(),
        1
    );
}

#[test]
fn legacy_writes_remain_compatible_and_rich_generation_changes_backfill() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("cache.db");
    let db = Db::open(&path).unwrap();
    let file = FileEntry::new(Path::new("/Game/Actor.h"), 1, SOURCE);
    let parsed = parsed(SOURCE);
    db.replace_file(&file, &parsed.symbols).unwrap();
    assert!(!db
        .needs_reindex(Path::new(&file.path), 1, &file.hash)
        .unwrap());
    assert!(db
        .needs_metadata_reindex(Path::new(&file.path), 1, &file.hash)
        .unwrap());
    assert!(db
        .find_with_metadata("Fire")
        .unwrap()
        .iter()
        .all(|s| s.metadata.is_none()));
    db.replace_file_with_metadata(&file, &parsed).unwrap();
    let raw = Connection::open(&path).unwrap();
    raw.execute_batch("UPDATE files SET parser_version = 0, metadata_version = 0")
        .unwrap();
    assert!(db
        .needs_reindex(Path::new(&file.path), 1, &file.hash)
        .unwrap());
    assert!(db
        .needs_metadata_reindex(Path::new(&file.path), 1, &file.hash)
        .unwrap());
    assert!(db.find_qualified("Game::AFirst::Fire").unwrap().is_empty());
    assert!(db
        .find_with_metadata("Fire")
        .unwrap()
        .iter()
        .all(|s| s.metadata.is_none()));
    assert_eq!(
        db.find("Fire").unwrap().len(),
        2,
        "old cache stays usable during backfill"
    );
    db.replace_file_with_metadata(&file, &parsed).unwrap();
    assert_eq!(
        raw.query_row("SELECT parser_version FROM files", [], |r| r
            .get::<_, u32>(0))
            .unwrap(),
        METADATA_VERSION
    );
    assert!(!db
        .needs_metadata_reindex(Path::new(&file.path), 1, &file.hash)
        .unwrap());
    // A symbol-only replacement must clear stale rich metadata, not misattach it.
    db.replace_file(&file, &parsed.symbols).unwrap();
    assert!(db.find_qualified("Game::AFirst::Fire").unwrap().is_empty());
    assert_eq!(
        raw.query_row("SELECT count(*) FROM symbol_metadata", [], |r| r
            .get::<_, i64>(0))
            .unwrap(),
        0
    );
}

#[test]
fn failed_replacement_preserves_old_fingerprint_symbols_and_metadata() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("cache.db");
    let db = Db::open(&path).unwrap();
    let file = FileEntry::new(Path::new("/Game/Actor.h"), 1, SOURCE);
    let parsed = parsed(SOURCE);
    db.replace_file_with_metadata(&file, &parsed).unwrap();
    let before = db.in_file_with_metadata(Path::new(&file.path)).unwrap();
    let mut invalid = parsed.clone();
    invalid.metadata.pop();
    assert!(matches!(
        db.replace_file_with_metadata(&file, &invalid),
        Err(DbError::InvalidMetadata(_))
    ));
    let raw = Connection::open(&path).unwrap();
    raw.execute_batch("CREATE TRIGGER fail_metadata BEFORE INSERT ON symbol_metadata BEGIN SELECT RAISE(ABORT, 'forced failure'); END;").unwrap();
    let changed = FileEntry {
        mtime: 2,
        hash: "changed".into(),
        ..file.clone()
    };
    assert!(db.replace_file_with_metadata(&changed, &parsed).is_err());
    assert_eq!(
        db.in_file_with_metadata(Path::new(&file.path)).unwrap(),
        before
    );
    assert!(!db
        .needs_metadata_reindex(Path::new(&file.path), 1, &file.hash)
        .unwrap());
}

#[test]
fn empty_or_all_unknown_metadata_can_be_marked_current() {
    let dir = tempfile::tempdir().unwrap();
    let db = Db::open(dir.path().join("cache.db")).unwrap();
    for source in ["", "UFUNCTION() int NotAFunction;"] {
        let parsed = parsed(source);
        let file = FileEntry::new(Path::new("/Game/Unknown.h"), 1, source);
        db.replace_file_with_metadata(&file, &parsed).unwrap();
        assert!(!db
            .needs_metadata_reindex(Path::new(&file.path), 1, &file.hash)
            .unwrap());
        assert!(db.members_of("Unknown").unwrap().is_empty());
    }
}

#[test]
fn rich_read_snapshot_never_mixes_generations_during_replacement() {
    let dir = tempfile::tempdir().unwrap();
    let db = Db::open(dir.path().join("cache.db")).unwrap();
    let file = FileEntry::new(Path::new("/Game/Concurrent.h"), 1, SOURCE);
    let mut old = parsed(SOURCE);
    let mut new = old.clone();
    for (symbol, metadata) in old.symbols.iter_mut().zip(&mut old.metadata) {
        symbol.bases = vec!["OldBase".into()];
        metadata.documentation = Some("old docs".into());
    }
    for (symbol, metadata) in new.symbols.iter_mut().zip(&mut new.metadata) {
        symbol.bases = vec!["NewBase".into()];
        metadata.documentation = Some("new docs".into());
    }
    db.replace_file_with_metadata(&file, &old).unwrap();
    let writer = {
        let db = db.clone();
        std::thread::spawn(move || {
            for i in 0..60 {
                db.replace_file_with_metadata(&file, if i % 2 == 0 { &new } else { &old })
                    .unwrap();
            }
        })
    };
    for _ in 0..120 {
        let rows = db.find_with_metadata("Fire").unwrap();
        assert_eq!(rows.len(), 2);
        for row in &rows {
            let expected = match row.symbol.bases[0].as_str() {
                "OldBase" => "old docs",
                "NewBase" => "new docs",
                other => panic!("{other}"),
            };
            assert_eq!(
                row.metadata.as_ref().unwrap().documentation.as_deref(),
                Some(expected)
            );
        }
        assert_eq!(rows[0].symbol.bases, rows[1].symbol.bases);
    }
    writer.join().unwrap();
}

#[test]
fn concurrent_open_serializes_migrations() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("cache.db");
    legacy_database(&path, 0);
    let barrier = std::sync::Arc::new(std::sync::Barrier::new(3));
    let handles: Vec<_> = (0..3)
        .map(|_| {
            let path = path.clone();
            let barrier = barrier.clone();
            std::thread::spawn(move || {
                barrier.wait();
                let db = Db::open(path).unwrap();
                assert_eq!(db.find("Value").unwrap().len(), 1);
            })
        })
        .collect();
    for handle in handles {
        handle.join().unwrap();
    }
}
