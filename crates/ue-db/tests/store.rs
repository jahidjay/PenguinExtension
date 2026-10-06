//! End-to-end checks against a real on-disk database.
//!
//! These use temp files rather than `:memory:` on purpose: an in-memory
//! `SqliteConnectionManager` hands every pooled connection its *own* empty
//! database, which would quietly make the pooling tests meaningless.

use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::thread;

use tempfile::TempDir;
use ue_db::{content_hash, Db, FileEntry};
use ue_parser::{SymbolKind, UnrealParser};

/// A database in a fresh temp directory. The `TempDir` is returned so the
/// caller keeps it alive — dropping it deletes the file out from under the pool.
fn temp_db() -> (TempDir, Db) {
    let dir = tempfile::tempdir().expect("temp dir");
    let db = Db::open(dir.path().join("penguin.db")).expect("open");
    (dir, db)
}

fn parse(source: &str) -> Vec<ue_parser::UnrealSymbol> {
    let mut parser = UnrealParser::new().expect("parser");
    parser.parse(source).symbols
}

/// Parses `source` and stores it as `path`, returning the file id.
fn index(db: &Db, path: &str, source: &str) -> i64 {
    let entry = FileEntry::new(Path::new(path), 1, source);
    db.replace_file(&entry, &parse(source)).expect("replace")
}

const ACTOR: &str = r#"
#pragma once
#include "CoreMinimal.h"
#include "MyActor.generated.h"

UCLASS(Blueprintable)
class MYGAME_API AMyActor : public AActor, public IMyInterface
{
    GENERATED_BODY()

public:
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category = "Stats")
    float Health;

    UFUNCTION(BlueprintCallable)
    void Fire();
};
"#;

const PAWN: &str = r#"
#pragma once
#include "MyPawn.generated.h"

UCLASS()
class MYGAME_API AMyPawn : public AMyActor
{
    GENERATED_BODY()

    UPROPERTY()
    int32 Ammo;
};
"#;

#[test]
fn symbols_round_trip_with_specifiers_and_bases() {
    let (_dir, db) = temp_db();
    index(&db, "/Game/MyActor.h", ACTOR);

    let hits = db.find("AMyActor").expect("find");
    assert_eq!(hits.len(), 1, "exactly one AMyActor");

    let hit = &hits[0];
    assert_eq!(hit.file, "/Game/MyActor.h");
    assert_eq!(hit.symbol.kind, SymbolKind::Class);
    assert_eq!(hit.symbol.macro_name, "UCLASS");
    assert_eq!(hit.symbol.bases, vec!["AActor", "IMyInterface"]);
    assert!(hit.symbol.has_specifier("Blueprintable"));
    assert!(hit.symbol.line > 1, "line survived the round trip");

    let health = db.find("Health").expect("find");
    assert_eq!(health.len(), 1);
    assert_eq!(health[0].symbol.kind, SymbolKind::Property);
    assert_eq!(health[0].symbol.type_name.as_deref(), Some("float"));
    // The parser strips the surrounding quotes, so what round-trips is the
    // bare string.
    assert_eq!(
        health[0].symbol.specifier_value("Category"),
        Some("Stats"),
        "the category value came back intact"
    );
}

#[test]
fn specifier_order_is_preserved() {
    let (_dir, db) = temp_db();
    index(&db, "/Game/MyActor.h", ACTOR);

    let health = db.find("Health").expect("find").remove(0);
    let keys: Vec<&str> = health
        .symbol
        .specifiers
        .iter()
        .map(|s| s.key.as_str())
        .collect();
    assert_eq!(keys, vec!["EditAnywhere", "BlueprintReadWrite", "Category"]);
}

#[test]
fn reindexing_replaces_instead_of_duplicating() {
    let (_dir, db) = temp_db();
    let first = index(&db, "/Game/MyActor.h", ACTOR);

    // The legacy extension appended on re-index, leaving stale symbols behind
    // (PLUGIN_STATE_REPORT §2.8). Same path, different contents.
    let trimmed = ACTOR.replace("    UFUNCTION(BlueprintCallable)\n    void Fire();\n", "");
    let second = index(&db, "/Game/MyActor.h", &trimmed);

    assert_eq!(first, second, "the same file row was reused");
    assert_eq!(db.find("AMyActor").expect("find").len(), 1);
    assert!(
        db.find("Fire").expect("find").is_empty(),
        "a symbol the header no longer declares must be gone"
    );
    assert_eq!(db.stats().expect("stats").files, 1);
}

#[test]
fn removing_a_file_cascades_to_its_symbols() {
    let (_dir, db) = temp_db();
    index(&db, "/Game/MyActor.h", ACTOR);
    assert!(db.stats().expect("stats").symbols > 0);

    assert!(db
        .remove_file(Path::new("/Game/MyActor.h"))
        .expect("remove"));
    assert!(!db
        .remove_file(Path::new("/Game/MyActor.h"))
        .expect("remove"));

    let stats = db.stats().expect("stats");
    assert_eq!(stats.files, 0);
    assert_eq!(stats.symbols, 0, "cascade cleared the symbols too");
}

#[test]
fn needs_reindex_tracks_mtime_and_hash() {
    let (_dir, db) = temp_db();
    let path = Path::new("/Game/MyActor.h");
    let hash = content_hash(ACTOR);

    assert!(db.needs_reindex(path, 1, &hash).expect("unseen"));

    db.replace_file(&FileEntry::new(path, 1, ACTOR), &parse(ACTOR))
        .expect("replace");

    assert!(!db.needs_reindex(path, 1, &hash).expect("unchanged"));
    assert!(db.needs_reindex(path, 2, &hash).expect("touched"));
    assert!(db
        .needs_reindex(path, 1, &content_hash("something else"))
        .expect("edited"));
}

#[test]
fn prefix_search_is_case_insensitive_and_escapes_wildcards() {
    let (_dir, db) = temp_db();
    index(&db, "/Game/MyActor.h", ACTOR);
    index(&db, "/Game/MyPawn.h", PAWN);

    let names: Vec<String> = db
        .prefix("amy", 10)
        .expect("prefix")
        .into_iter()
        .map(|h| h.symbol.name)
        .collect();
    assert_eq!(names, vec!["AMyPawn", "AMyActor"], "shorter name first");

    assert_eq!(db.prefix("AMyActor", 10).expect("prefix").len(), 1);
    assert_eq!(db.prefix("Z", 10).expect("prefix").len(), 0);

    // `_` is a LIKE wildcard; unescaped, "A_Actor" would match "AMyActor".
    assert!(
        db.prefix("A_", 10).expect("prefix").is_empty(),
        "underscore must be matched literally"
    );
    assert!(db.prefix("%", 10).expect("prefix").is_empty());
}

#[test]
fn prefix_respects_the_limit() {
    let (_dir, db) = temp_db();
    index(&db, "/Game/MyActor.h", ACTOR);
    index(&db, "/Game/MyPawn.h", PAWN);
    assert_eq!(db.prefix("A", 1).expect("prefix").len(), 1);
}

#[test]
fn file_and_kind_queries_scope_correctly() {
    let (_dir, db) = temp_db();
    index(&db, "/Game/MyActor.h", ACTOR);
    index(&db, "/Game/MyPawn.h", PAWN);

    let in_pawn = db.in_file(Path::new("/Game/MyPawn.h")).expect("in_file");
    let names: Vec<&str> = in_pawn.iter().map(|h| h.symbol.name.as_str()).collect();
    assert_eq!(names, vec!["AMyPawn", "Ammo"], "source order");

    let classes = db.of_kind(SymbolKind::Class, 50).expect("of_kind");
    let names: Vec<&str> = classes.iter().map(|h| h.symbol.name.as_str()).collect();
    assert_eq!(names, vec!["AMyActor", "AMyPawn"]);

    assert!(db
        .of_kind(SymbolKind::Property, 50)
        .expect("of_kind")
        .iter()
        .all(|h| h.symbol.kind == SymbolKind::Property));
}

#[test]
fn windows_paths_normalize_to_the_same_row() {
    let (_dir, db) = temp_db();
    index(&db, r"C:\Game\MyActor.h", ACTOR);

    // Same header reached through a URI-derived path.
    assert_eq!(
        db.in_file(Path::new("C:/Game/MyActor.h"))
            .expect("in_file")
            .len(),
        db.in_file(Path::new(r"C:\Game\MyActor.h"))
            .expect("in_file")
            .len()
    );
    assert_eq!(db.files().expect("files"), vec!["C:/Game/MyActor.h"]);
}

#[test]
fn inheritance_walks_breadth_first_and_derived_lists_children() {
    let (_dir, db) = temp_db();
    index(&db, "/Game/MyActor.h", ACTOR);
    index(&db, "/Game/MyPawn.h", PAWN);

    let chain = db.inheritance_chain("AMyPawn").expect("chain");
    assert_eq!(
        chain,
        vec!["AMyActor", "AActor", "IMyInterface"],
        "direct parent first, then its bases in declaration order"
    );

    let derived: Vec<String> = db
        .derived_from("AMyActor")
        .expect("derived")
        .into_iter()
        .map(|h| h.symbol.name)
        .collect();
    assert_eq!(derived, vec!["AMyPawn"]);
    assert!(db.derived_from("ANobody").expect("derived").is_empty());
}

#[test]
fn a_cyclic_hierarchy_terminates() {
    let (_dir, db) = temp_db();
    // Mutually derived classes — impossible in real C++, trivial to produce
    // from a half-indexed workspace, and an infinite loop in the legacy
    // InheritanceChain walk (PLUGIN_STATE_REPORT §2.3).
    index(
        &db,
        "/Game/A.h",
        "UCLASS()\nclass AAlpha : public ABeta { GENERATED_BODY() };\n",
    );
    index(
        &db,
        "/Game/B.h",
        "UCLASS()\nclass ABeta : public AAlpha { GENERATED_BODY() };\n",
    );

    let chain = db.inheritance_chain("AAlpha").expect("chain");
    assert_eq!(chain, vec!["ABeta"], "the start class is not revisited");
}

#[test]
fn search_matches_anywhere_in_the_name() {
    let (_dir, db) = temp_db();
    index(&db, "/Game/MyActor.h", ACTOR);

    let hits: Vec<String> = db
        .search("ealth", 10)
        .expect("search")
        .into_iter()
        .map(|h| h.symbol.name)
        .collect();
    assert_eq!(hits, vec!["Health"]);
}

#[test]
fn clear_empties_the_store_but_keeps_it_usable() {
    let (_dir, db) = temp_db();
    index(&db, "/Game/MyActor.h", ACTOR);
    db.clear().expect("clear");

    assert_eq!(db.stats().expect("stats"), Default::default());
    index(&db, "/Game/MyActor.h", ACTOR);
    assert_eq!(db.find("AMyActor").expect("find").len(), 1);
}

#[test]
fn readers_and_a_writer_share_the_pool() {
    // The legacy extension funneled everything through one `SQLiteConnection`,
    // so a query during indexing blocked or hit a disposed handle
    // (PLUGIN_STATE_REPORT §2.1/§2.2). WAL plus the pool is the fix; this only
    // asserts the combination doesn't deadlock or error under contention.
    let (_dir, db) = temp_db();
    index(&db, "/Game/MyActor.h", ACTOR);
    let db = Arc::new(db);

    let writer = {
        let db = Arc::clone(&db);
        thread::spawn(move || {
            for n in 0..25 {
                let path = PathBuf::from(format!("/Game/Gen{n}.h"));
                let source =
                    format!("UCLASS()\nclass AGen{n} : public AMyActor {{ GENERATED_BODY() }};\n");
                let entry = FileEntry::new(&path, n, &source);
                db.replace_file(&entry, &parse(&source)).expect("write");
            }
        })
    };

    let readers: Vec<_> = (0..4)
        .map(|_| {
            let db = Arc::clone(&db);
            thread::spawn(move || {
                for _ in 0..50 {
                    // Never empty: AMyActor is committed before the threads start.
                    assert!(!db.find("AMyActor").expect("read").is_empty());
                    db.prefix("A", 20).expect("read");
                    db.stats().expect("read");
                }
            })
        })
        .collect();

    writer.join().expect("writer");
    for reader in readers {
        reader.join().expect("reader");
    }

    assert_eq!(db.stats().expect("stats").files, 26);
    assert_eq!(db.derived_from("AMyActor").expect("derived").len(), 25);
}
