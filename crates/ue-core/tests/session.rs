use std::fs;
use std::path::Path;
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
};
use std::time::Duration;
use ue_core::*;

const HEADER: &str = "/// Actor docs
UCLASS()
class ATest : public ABase { UFUNCTION() void Run(int X); UFUNCTION() void Run(float X); };
";
fn config(root: &Path) -> SessionConfig {
    SessionConfig::new(vec![root.to_str().unwrap().into()], "test")
}
async fn wait(s: &WorkspaceSession, id: &str) -> JobStatus {
    tokio::time::timeout(Duration::from_secs(15), async {
        loop {
            let job = s.job_status(id).unwrap();
            if job.state.is_terminal() {
                return job;
            }
            tokio::time::sleep(Duration::from_millis(5)).await;
        }
    })
    .await
    .expect("job finishes")
}
async fn indexed(s: &WorkspaceSession) {
    let job = s.reindex().unwrap();
    let job = wait(s, &job.job_id).await;
    assert_eq!(job.state, JobState::Succeeded, "{job:?}");
    assert_eq!(
        job.result.unwrap()["report"]["errors"],
        serde_json::json!([])
    );
}
fn search(query: &str, limit: usize) -> SearchRequest {
    SearchRequest {
        api_version: API_VERSION,
        query: query.into(),
        limit,
    }
}

#[tokio::test]
async fn leases_release_and_namespaces_are_isolated_without_touching_legacy() {
    let dir = tempfile::tempdir().unwrap();
    fs::create_dir_all(dir.path().join(".vs/PenguinExtension")).unwrap();
    let legacy = dir.path().join(".vs/PenguinExtension/penguin_cache.db");
    fs::write(&legacy, b"legacy untouched").unwrap();
    let first = WorkspaceSession::open(config(dir.path())).await.unwrap();
    assert_eq!(
        WorkspaceSession::open(config(dir.path()))
            .await
            .err()
            .unwrap()
            .code,
        ErrorCode::Busy
    );
    let mut other_config = config(dir.path());
    other_config.adapter = "other".into();
    let other = WorkspaceSession::open(other_config).await.unwrap();
    assert_ne!(first.status().cache_path, other.status().cache_path);
    let original = first.status();
    first.close(false).await.unwrap();
    let next = WorkspaceSession::open(config(dir.path())).await.unwrap();
    assert_eq!(next.status().cache_path, original.cache_path);
    assert_ne!(next.status().session_id, original.session_id);
    assert_eq!(fs::read(&legacy).unwrap(), b"legacy untouched");
    next.close(true).await.unwrap();
    other.close(true).await.unwrap();
}

#[tokio::test]
async fn canonical_root_sets_aliases_and_deleted_files() {
    let dir = tempfile::tempdir().unwrap();
    let first = dir.path().join("Root");
    let second = dir.path().join("Second");
    fs::create_dir_all(first.join("Nested")).unwrap();
    fs::create_dir(&second).unwrap();
    let file = first.join("Nested/Test.h");
    fs::write(&file, HEADER).unwrap();
    let mut cfg = config(&first);
    cfg.roots.push(second.to_str().unwrap().into());
    let s = WorkspaceSession::open(cfg.clone()).await.unwrap();
    indexed(&s).await;
    let original = s.status().cache_namespace;
    cfg.roots.reverse();
    cfg.roots
        .push(first.join("Nested/..").to_str().unwrap().into());
    cfg.roots
        .push(first.join("Nested").to_str().unwrap().into());
    assert_eq!(
        WorkspaceSession::open(cfg.clone())
            .await
            .err()
            .unwrap()
            .code,
        ErrorCode::Busy
    );
    let alias = first.join("Nested/../Nested/Test.h");
    assert_eq!(
        s.file_symbols(FileSymbolsRequest {
            api_version: 1,
            file: alias.to_str().unwrap().into(),
            limit: 20
        })
        .await
        .unwrap()
        .symbols
        .len(),
        3
    );
    fs::remove_file(&file).unwrap();
    fs::remove_dir(first.join("Nested")).unwrap();
    let j = s
        .files_changed(FileChangesRequest {
            api_version: 1,
            files: vec![file.to_str().unwrap().into()],
        })
        .await
        .unwrap();
    assert_eq!(wait(&s, &j.job_id).await.state, JobState::Succeeded);
    assert!(s.search(search("", 20)).await.unwrap().symbols.is_empty());
    s.close(false).await.unwrap();
    // The redundant nested root must still exist to be valid at open time.
    fs::create_dir(first.join("Nested")).unwrap();
    let s = WorkspaceSession::open(cfg).await.unwrap();
    assert_eq!(s.status().cache_namespace, original);
    s.close(false).await.unwrap();
}

#[tokio::test]
async fn overload_references_metadata_limits_and_revisions() {
    let dir = tempfile::tempdir().unwrap();
    fs::write(dir.path().join("A.h"), HEADER).unwrap();
    fs::write(dir.path().join("B.h"), HEADER).unwrap();
    fs::write(
        dir.path().join("Base.h"),
        "UCLASS() class ABase : public UObject {};",
    )
    .unwrap();
    let s = WorkspaceSession::open(config(dir.path())).await.unwrap();
    indexed(&s).await;
    let hits = s.search(search("Run", 20)).await.unwrap();
    assert_eq!(hits.symbols.len(), 4);
    let keys: std::collections::HashSet<_> = hits
        .symbols
        .iter()
        .map(|s| &s.reference.symbol_key)
        .collect();
    assert_eq!(keys.len(), 4);
    for hit in &hits.symbols {
        let detail = s
            .details(DetailRequest {
                api_version: 1,
                reference: hit.reference.clone(),
            })
            .await
            .unwrap();
        assert_eq!(detail.symbol, *hit);
        assert!(detail.metadata.unwrap().signature.unwrap().contains("Run"));
    }
    let bounded = s.search(search("", 1)).await.unwrap();
    assert_eq!(bounded.symbols.len(), 1);
    assert!(bounded.truncated);
    let class = s
        .search(search("ATest", 20))
        .await
        .unwrap()
        .symbols
        .remove(0);
    let chain = s
        .inheritance(InheritanceRequest {
            api_version: 1,
            reference: class.reference,
            limit: 20,
        })
        .await
        .unwrap();
    assert_eq!(chain.bases, ["ABase", "UObject"]);
    let old = hits.symbols[0].reference.clone();
    indexed(&s).await;
    assert_eq!(
        s.details(DetailRequest {
            api_version: 1,
            reference: old.clone()
        })
        .await
        .unwrap_err()
        .code,
        ErrorCode::StaleReference
    );
    s.close(false).await.unwrap();
    let next = WorkspaceSession::open(config(dir.path())).await.unwrap();
    assert_eq!(
        next.details(DetailRequest {
            api_version: 1,
            reference: old
        })
        .await
        .unwrap_err()
        .code,
        ErrorCode::StaleReference
    );
    next.close(false).await.unwrap();
}

#[tokio::test]
async fn rejected_inputs_outside_paths_and_exclusions_never_mutate_sources() {
    let dir = tempfile::tempdir().unwrap();
    let root = dir.path().join("Game");
    fs::create_dir(&root).unwrap();
    let outside = dir.path().join("Outside.h");
    fs::write(&outside, HEADER).unwrap();
    let s = WorkspaceSession::open(config(&root)).await.unwrap();
    for request in [
        search("x", 0),
        search("x", 201),
        search(&"x".repeat(513), 1),
        SearchRequest {
            api_version: 2,
            query: "".into(),
            limit: 1,
        },
    ] {
        assert_eq!(
            s.search(request).await.unwrap_err().code,
            ErrorCode::InvalidInput
        );
    }
    for path in [
        &outside,
        &root.join("../Outside.h"),
        &dir.path().join("GameExtra/Missing.h"),
    ] {
        assert_eq!(
            s.files_changed(FileChangesRequest {
                api_version: 1,
                files: vec![path.to_str().unwrap().into()]
            })
            .await
            .unwrap_err()
            .code,
            ErrorCode::OutsideRoots
        );
    }
    for name in [
        ".git/X.h",
        "Intermediate/X.h",
        "Saved/X.h",
        "X.generated.h",
        "X.cpp",
    ] {
        assert_eq!(
            s.file_symbols(FileSymbolsRequest {
                api_version: 1,
                file: root.join(name).to_str().unwrap().into(),
                limit: 1
            })
            .await
            .unwrap_err()
            .code,
            ErrorCode::ExcludedPath
        );
    }
    assert!(s
        .files_changed(FileChangesRequest {
            api_version: 1,
            files: Vec::new()
        })
        .await
        .is_err());
    assert!(s
        .files_changed(FileChangesRequest {
            api_version: 1,
            files: vec!["x".into(); 257]
        })
        .await
        .is_err());
    assert!(s
        .file_symbols(FileSymbolsRequest {
            api_version: 1,
            file: "relative.h".into(),
            limit: 1
        })
        .await
        .is_err());
    assert_eq!(fs::read_to_string(outside).unwrap(), HEADER);
    s.close(false).await.unwrap();
    assert_eq!(s.reindex().unwrap_err().code, ErrorCode::Closed);
    assert_eq!(
        s.search(search("", 1)).await.unwrap_err().code,
        ErrorCode::Closed
    );
}

#[tokio::test]
async fn queue_cancel_retention_and_close_drain() {
    let dir = tempfile::tempdir().unwrap();
    let mut cfg = config(dir.path());
    cfg.queue_capacity = 1;
    cfg.retained_jobs = 2;
    let s = WorkspaceSession::open(cfg.clone()).await.unwrap();
    let started = Arc::new(AtomicBool::new(false));
    let inside = started.clone();
    let running = s
        .submit_job("cooperative", move |ctx| {
            inside.store(true, Ordering::Release);
            while !ctx.cancellation.is_cancelled() {
                std::thread::sleep(Duration::from_millis(2));
            }
            // Simulate a late provider success: cancellation still suppresses it.
            Ok(serde_json::json!({"late":true}))
        })
        .unwrap();
    tokio::time::timeout(Duration::from_secs(5), async {
        while !started.load(Ordering::Acquire) {
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
    })
    .await
    .unwrap();
    let queued = s.reindex().unwrap();
    assert_eq!(s.reindex().unwrap_err().code, ErrorCode::QueueFull);
    s.cancel_job(&queued.job_id).unwrap();
    s.cancel_job(&running.job_id).unwrap();
    assert_eq!(wait(&s, &running.job_id).await.state, JobState::Cancelled);
    assert!(s.job_status(&running.job_id).unwrap().result.is_none());
    assert_eq!(wait(&s, &queued.job_id).await.state, JobState::Cancelled);
    let successful = s
        .submit_job("example", |_| Ok(serde_json::json!({"answer":42})))
        .unwrap();
    assert_eq!(
        wait(&s, &successful.job_id).await.state,
        JobState::Succeeded
    );
    assert_eq!(
        s.job_status(&running.job_id).unwrap_err().code,
        ErrorCode::NotFound
    );
    let final_job = s.reindex().unwrap();
    let (a, b) = tokio::join!(s.close(false), s.close(false));
    a.unwrap();
    b.unwrap();
    assert_eq!(
        s.job_status(&final_job.job_id).unwrap().state,
        JobState::Succeeded
    );
    let reopened = WorkspaceSession::open(cfg).await.unwrap();
    reopened.close(true).await.unwrap();
}

#[tokio::test]
async fn oversized_and_panicking_jobs_fail_without_poisoning_worker() {
    let dir = tempfile::tempdir().unwrap();
    let s = WorkspaceSession::open(config(dir.path())).await.unwrap();
    let huge = s
        .submit_job("oversized", |_| {
            Ok(serde_json::json!("x".repeat(MAX_JOB_RESULT_BYTES + 1)))
        })
        .unwrap();
    let result = wait(&s, &huge.job_id).await;
    assert_eq!(result.state, JobState::Failed);
    assert_eq!(result.error.unwrap().code, ErrorCode::ResultTooLarge);
    let panic = s.submit_job("panic", |_| panic!("test job panic")).unwrap();
    assert_eq!(wait(&s, &panic.job_id).await.state, JobState::Failed);
    indexed(&s).await;
    s.close(true).await.unwrap();
}

#[tokio::test]
async fn close_cancel_discards_queued_work_and_releases_lease() {
    let dir = tempfile::tempdir().unwrap();
    let s = WorkspaceSession::open(config(dir.path())).await.unwrap();
    let began = Arc::new(AtomicBool::new(false));
    let b = began.clone();
    let j = s
        .submit_job("cancel", move |ctx| {
            b.store(true, Ordering::Release);
            while !ctx.cancellation.is_cancelled() {
                std::thread::sleep(Duration::from_millis(2));
            }
            ctx.cancellation.check()?;
            Ok(serde_json::Value::Null)
        })
        .unwrap();
    tokio::time::timeout(Duration::from_secs(5), async {
        while !began.load(Ordering::Acquire) {
            tokio::time::sleep(Duration::from_millis(2)).await;
        }
    })
    .await
    .unwrap();
    let called = Arc::new(AtomicBool::new(false));
    let c = called.clone();
    let queued = s
        .submit_job("queued", move |_| {
            c.store(true, Ordering::Release);
            Ok(serde_json::Value::Null)
        })
        .unwrap();
    s.close(true).await.unwrap();
    assert!(!called.load(Ordering::Acquire));
    assert_eq!(s.job_status(&j.job_id).unwrap().state, JobState::Cancelled);
    assert_eq!(
        s.job_status(&queued.job_id).unwrap().state,
        JobState::Cancelled
    );
    let reopened = WorkspaceSession::open(config(dir.path())).await.unwrap();
    reopened.close(true).await.unwrap();
}

#[test]
fn transport_is_versioned_camel_case_and_strict_on_inputs() {
    let request: SearchRequest =
        serde_json::from_value(serde_json::json!({"apiVersion":1,"query":"Actor"})).unwrap();
    assert_eq!(request.limit, 50);
    assert!(serde_json::from_value::<SearchRequest>(
        serde_json::json!({"apiVersion":1,"query":"Actor","unexpected":true})
    )
    .is_err());
    assert_eq!(
        serde_json::to_value(CoreError::new(ErrorCode::StaleReference, "old")).unwrap(),
        serde_json::json!({"apiVersion":1,"code":"staleReference","message":"old"})
    );
}

#[test]
fn cancelled_scan_does_not_prune_missing_files() {
    let dir = tempfile::tempdir().unwrap();
    let root = dir.path().join("Game");
    fs::create_dir(&root).unwrap();
    let file = root.join("Gone.h");
    fs::write(&file, HEADER).unwrap();
    let db = ue_db::Db::open(dir.path().join("cache.db")).unwrap();
    assert_eq!(
        ue_core::index::index_workspace(&db, std::slice::from_ref(&root)).indexed,
        1
    );
    fs::remove_file(&file).unwrap();
    let cancelled = AtomicBool::new(true);
    let report = ue_core::index::index_workspace_cancellable(&db, &[root], &cancelled);
    assert_eq!(report.removed, 0);
    assert_eq!(db.stats().unwrap().files, 1);
}

#[tokio::test]
async fn indexing_errors_fail_job_and_preserve_old_symbols() {
    let dir = tempfile::tempdir().unwrap();
    let file = dir.path().join("Keep.h");
    fs::write(&file, HEADER).unwrap();
    let s = WorkspaceSession::open(config(dir.path())).await.unwrap();
    indexed(&s).await;
    fs::write(&file, [0xff, 0]).unwrap();
    let job = s.reindex().unwrap();
    let job = wait(&s, &job.job_id).await;
    assert_eq!(job.state, JobState::Failed);
    assert_eq!(job.error.unwrap().code, ErrorCode::IndexFailed);
    assert_eq!(s.search(search("Run", 20)).await.unwrap().symbols.len(), 2);
    s.close(true).await.unwrap();
}

#[tokio::test]
async fn drop_releases_the_lease_without_explicit_close() {
    let dir = tempfile::tempdir().unwrap();
    let s = WorkspaceSession::open(config(dir.path())).await.unwrap();
    drop(s);
    tokio::time::timeout(Duration::from_secs(10), async {
        loop {
            match WorkspaceSession::open(config(dir.path())).await {
                Ok(next) => {
                    next.close(true).await.unwrap();
                    break;
                }
                Err(e) if e.code == ErrorCode::Busy => {
                    tokio::time::sleep(Duration::from_millis(5)).await
                }
                Err(e) => panic!("{e:?}"),
            }
        }
    })
    .await
    .unwrap();
}

#[tokio::test]
async fn root_sets_isolate_contents_and_references() {
    let dir = tempfile::tempdir().unwrap();
    let a = dir.path().join("A");
    let b = dir.path().join("B");
    fs::create_dir(&a).unwrap();
    fs::create_dir(&b).unwrap();
    fs::write(a.join("A.h"), "UCLASS() class AOne {};").unwrap();
    fs::write(b.join("B.h"), "UCLASS() class ATwo {};").unwrap();
    let one = WorkspaceSession::open(config(&a)).await.unwrap();
    let mut cfg = config(&a);
    cfg.roots.push(b.to_str().unwrap().into());
    let both = WorkspaceSession::open(cfg).await.unwrap();
    indexed(&one).await;
    indexed(&both).await;
    assert!(one
        .search(search("ATwo", 10))
        .await
        .unwrap()
        .symbols
        .is_empty());
    assert_eq!(
        both.search(search("ATwo", 10)).await.unwrap().symbols.len(),
        1
    );
    assert_ne!(one.status().cache_namespace, both.status().cache_namespace);
    let reference = both
        .search(search("AOne", 10))
        .await
        .unwrap()
        .symbols
        .remove(0)
        .reference;
    assert_eq!(
        one.details(DetailRequest {
            api_version: 1,
            reference
        })
        .await
        .unwrap_err()
        .code,
        ErrorCode::StaleReference
    );
    one.close(false).await.unwrap();
    both.close(false).await.unwrap();
}

#[test]
fn lease_child_process() {
    let Ok(root) = std::env::var("UE_CORE_TEST_LEASE_ROOT") else {
        return;
    };
    let runtime = tokio::runtime::Runtime::new().unwrap();
    runtime.block_on(async {
        let cfg = SessionConfig::new(vec![root], "test");
        let error = WorkspaceSession::open(cfg)
            .await
            .err()
            .expect("other process owns writer lease");
        assert_eq!(error.code, ErrorCode::Busy);
    });
}

#[tokio::test]
async fn os_lease_blocks_another_process() {
    let dir = tempfile::tempdir().unwrap();
    let s = WorkspaceSession::open(config(dir.path())).await.unwrap();
    let root = dir.path().to_owned();
    let status = tokio::task::spawn_blocking(move || {
        std::process::Command::new(std::env::current_exe().unwrap())
            .args(["--exact", "lease_child_process", "--nocapture"])
            .env("UE_CORE_TEST_LEASE_ROOT", root)
            .status()
            .unwrap()
    })
    .await
    .unwrap();
    assert!(status.success());
    s.close(false).await.unwrap();
}

#[tokio::test]
async fn roots_are_validated_and_missing_parent_aliases_rejected() {
    let dir = tempfile::tempdir().unwrap();
    assert_eq!(
        WorkspaceSession::open(SessionConfig::new(Vec::new(), "test"))
            .await
            .err()
            .unwrap()
            .code,
        ErrorCode::InvalidInput
    );
    assert_eq!(
        WorkspaceSession::open(SessionConfig::new(vec!["relative".into()], "test"))
            .await
            .err()
            .unwrap()
            .code,
        ErrorCode::InvalidInput
    );
    assert!(WorkspaceSession::open(SessionConfig::new(
        vec![dir.path().to_str().unwrap().into()],
        "../escape"
    ))
    .await
    .is_err());
    let s = WorkspaceSession::open(config(dir.path())).await.unwrap();
    assert!(s
        .files_changed(FileChangesRequest {
            api_version: 1,
            files: vec![dir.path().join("missing/../X.h").to_str().unwrap().into()]
        })
        .await
        .is_err());
    s.close(false).await.unwrap();
}

#[test]
fn lease_owner_child_process() {
    let Ok(root) = std::env::var("UE_CORE_TEST_OWNER_ROOT") else {
        return;
    };
    let runtime = tokio::runtime::Runtime::new().unwrap();
    let _session = runtime
        .block_on(WorkspaceSession::open(SessionConfig::new(
            vec![root.clone()],
            "test",
        )))
        .unwrap();
    fs::write(Path::new(&root).join("lease-ready"), "ready").unwrap();
    loop {
        std::thread::sleep(Duration::from_secs(60));
    }
}

#[test]
fn os_lease_is_released_after_owner_process_is_killed() {
    let dir = tempfile::tempdir().unwrap();
    let mut child = std::process::Command::new(std::env::current_exe().unwrap())
        .args(["--exact", "lease_owner_child_process", "--nocapture"])
        .env("UE_CORE_TEST_OWNER_ROOT", dir.path())
        .spawn()
        .unwrap();
    let deadline = std::time::Instant::now() + Duration::from_secs(10);
    while !dir.path().join("lease-ready").exists() && std::time::Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(5));
    }
    let ready = dir.path().join("lease-ready").exists();
    child.kill().unwrap();
    child.wait().unwrap();
    assert!(ready, "child acquired lease");
    let runtime = tokio::runtime::Runtime::new().unwrap();
    runtime.block_on(async {
        let session = WorkspaceSession::open(config(dir.path())).await.unwrap();
        session.close(false).await.unwrap();
    });
}
