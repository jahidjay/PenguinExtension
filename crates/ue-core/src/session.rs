use crate::{
    dto::*,
    index,
    jobs::{self, JobContext, Shared, Work},
};
use fs2::FileExt;
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::collections::{BTreeSet, HashSet, VecDeque};
use std::fs::{self, File, OpenOptions};
use std::path::{Component, Path, PathBuf};
use std::sync::{Arc, Mutex, RwLock};
use std::thread::JoinHandle;
use ue_db::{Db, StoredSymbol};

type Database = Arc<RwLock<Option<Db>>>;

/// Cloneable leased disk-backed workspace. Opening does not start indexing.
/// Call `reindex` explicitly. `close(false)` drains, `close(true)` cancels/drains.
/// Drop initiates cancellation; await `close` to guarantee lease release.
#[derive(Clone)]
pub struct WorkspaceSession {
    inner: Arc<Inner>,
}
struct Inner {
    id: String,
    roots: Vec<PathBuf>,
    adapter: String,
    namespace: String,
    cache_path: PathBuf,
    db: Database,
    shared: Arc<Shared>,
    worker: Mutex<Option<JoinHandle<()>>>,
    queries: Arc<tokio::sync::Semaphore>,
}
impl Drop for Inner {
    fn drop(&mut self) {
        self.shared.begin_close(true);
    }
}
impl WorkspaceSession {
    pub async fn open(config: SessionConfig) -> Result<Self> {
        blocking(move || Self::open_blocking(config)).await
    }
    fn open_blocking(config: SessionConfig) -> Result<Self> {
        version(config.api_version)?;
        if config.roots.is_empty() || config.roots.len() > MAX_ROOTS {
            return Err(invalid("roots must contain 1..=16 directories"));
        }
        if config.adapter.is_empty()
            || config.adapter.len() > 32
            || !config
                .adapter
                .bytes()
                .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'-' || b == b'_')
        {
            return Err(invalid(
                "adapter must be 1..=32 lowercase ASCII letters, digits, _ or -",
            ));
        }
        if !(1..=64).contains(&config.queue_capacity) || !(1..=64).contains(&config.retained_jobs) {
            return Err(invalid("queueCapacity and retainedJobs must be 1..=64"));
        }
        let mut roots = BTreeSet::new();
        for root in &config.roots {
            let path = input_path(root)?;
            let root = index::resolve_path(&path)?.ok_or_else(|| {
                CoreError::new(ErrorCode::ExcludedPath, "root is excluded or redirected")
            })?;
            if index::resolve_path(&root)?.as_ref() != Some(&root) {
                return Err(CoreError::new(
                    ErrorCode::ExcludedPath,
                    "canonical root is excluded",
                ));
            }
            if !fs::metadata(&root)?.is_dir() {
                return Err(invalid("root is not a directory"));
            }
            roots.insert(root);
        }
        let mut unique: Vec<PathBuf> = Vec::new();
        for root in roots {
            if !unique.iter().any(|parent| root.starts_with(parent)) {
                unique.push(root);
            }
        }
        let mut hash = Sha256::new();
        for root in &unique {
            let key = ue_db::path_key(root);
            hash.update((key.len() as u64).to_le_bytes());
            hash.update(key.as_bytes());
        }
        let namespace = format!("{}-{:x}", config.adapter, hash.finalize());
        let cache_dir = unique[0]
            .join(".vs")
            .join("PenguinExtension")
            .join("core-v1")
            .join(&namespace);
        reject_redirects(&cache_dir)?;
        fs::create_dir_all(&cache_dir)?;
        reject_redirects(&cache_dir)?;
        let lock_path = cache_dir.join("writer.lock");
        reject_redirects(&lock_path)?;
        let lease = OpenOptions::new()
            .create(true)
            .truncate(false)
            .read(true)
            .write(true)
            .open(lock_path)?;
        lease.try_lock_exclusive().map_err(|e| {
            if e.kind() == std::io::ErrorKind::WouldBlock
                || e.raw_os_error() == fs2::lock_contended_error().raw_os_error()
            {
                CoreError::new(
                    ErrorCode::Busy,
                    "cache namespace already has an active writer",
                )
            } else {
                CoreError::from(e)
            }
        })?;
        let cache_path = cache_dir.join("penguin_cache.db");
        for path in [
            cache_path.clone(),
            cache_dir.join("penguin_cache.db-wal"),
            cache_dir.join("penguin_cache.db-shm"),
        ] {
            reject_redirects(&path)?;
        }
        let db = Db::open(&cache_path)?;
        let stats = db.stats()?;
        let db = Arc::new(RwLock::new(Some(db)));
        let shared = Arc::new(Shared::new(
            config.queue_capacity,
            config.retained_jobs,
            stats.files,
            stats.symbols,
        ));
        let id = uuid::Uuid::new_v4().to_string();
        let worker = {
            let db = db.clone();
            let shared = shared.clone();
            let roots = unique.clone();
            let id = id.clone();
            std::thread::Builder::new()
                .name("ue-core-index".into())
                .spawn(move || worker_loop(db, shared, roots, id, lease))?
        };
        Ok(Self {
            inner: Arc::new(Inner {
                id,
                roots: unique,
                adapter: config.adapter,
                namespace,
                cache_path,
                db,
                shared,
                worker: Mutex::new(Some(worker)),
                queries: Arc::new(tokio::sync::Semaphore::new(8)),
            }),
        })
    }
    /// In-memory snapshot, including while indexing. Counts update after jobs.
    pub fn status(&self) -> SessionStatus {
        let s = self.inner.shared.lock();
        SessionStatus {
            api_version: API_VERSION,
            session_id: self.inner.id.clone(),
            revision: s.revision,
            roots: self
                .inner
                .roots
                .iter()
                .map(|p| ue_db::path_key(p))
                .collect(),
            adapter: self.inner.adapter.clone(),
            cache_namespace: self.inner.namespace.clone(),
            cache_path: ue_db::path_key(&self.inner.cache_path),
            closed: s.closing,
            indexed_files: s.indexed_files,
            indexed_symbols: s.indexed_symbols,
            queued_jobs: s
                .jobs
                .values()
                .filter(|j| j.status.state == JobState::Queued)
                .count(),
            running_jobs: s
                .jobs
                .values()
                .filter(|j| j.status.state == JobState::Running)
                .count(),
            retained_jobs: s.finished.len(),
        }
    }
    /// Submit promptly without awaiting the scan.
    pub fn reindex(&self) -> Result<JobStatus> {
        self.inner
            .shared
            .enqueue(&self.inner.id, "indexWorkspace".into(), Work::Index)
    }
    /// Validate all paths on a blocking worker before queuing any mutations.
    /// Missing paths resolve through their closest surviving ancestor.
    pub async fn files_changed(&self, request: FileChangesRequest) -> Result<JobStatus> {
        version(request.api_version)?;
        if request.files.is_empty() || request.files.len() > MAX_CHANGED_FILES {
            return Err(invalid("files must contain 1..=256 paths"));
        }
        let permit = self
            .inner
            .queries
            .clone()
            .try_acquire_owned()
            .map_err(|_| CoreError::new(ErrorCode::Busy, "too many concurrent queries"))?;
        let this = self.clone();
        blocking(move || {
            let _permit = permit;
            this.ensure_open()?;
            let mut files = BTreeSet::new();
            for file in request.files {
                files.insert(this.checked_file(&file)?);
            }
            this.inner.shared.enqueue(
                &this.inner.id,
                "indexFiles".into(),
                Work::Files(files.into_iter().collect()),
            )
        })
        .await
    }
    /// Bounded cooperative work runs on the serial blocking worker. Closures own
    /// immutable inputs and check the token. Core never invokes AI itself.
    pub fn submit_job<F>(&self, kind: impl Into<String>, task: F) -> Result<JobStatus>
    where
        F: FnOnce(JobContext) -> Result<Value> + Send + 'static,
    {
        let kind = kind.into();
        if kind.is_empty()
            || kind.len() > 64
            || !kind
                .bytes()
                .all(|b| b.is_ascii_alphanumeric() || b == b'_' || b == b'-')
        {
            return Err(invalid("invalid job kind"));
        }
        self.inner
            .shared
            .enqueue(&self.inner.id, kind, Work::Custom(Box::new(task)))
    }
    pub fn job_status(&self, id: &str) -> Result<JobStatus> {
        validate_job_id(id)?;
        self.inner
            .shared
            .lock()
            .jobs
            .get(id)
            .map(|j| j.status.clone())
            .ok_or_else(|| CoreError::new(ErrorCode::NotFound, "unknown or evicted job"))
    }
    pub fn cancel_job(&self, id: &str) -> Result<JobStatus> {
        validate_job_id(id)?;
        let mut s = self.inner.shared.lock();
        let job = s
            .jobs
            .get_mut(id)
            .ok_or_else(|| CoreError::new(ErrorCode::NotFound, "unknown or evicted job"))?;
        if !job.status.state.is_terminal() {
            job.token.cancel();
        }
        Ok(job.status.clone())
    }
    /// Stop accepting work; wait off-executor for DB and OS lease release.
    /// Repeated/concurrent calls are supported.
    pub async fn close(&self, cancel_pending: bool) -> Result<()> {
        self.inner.shared.begin_close(cancel_pending);
        let this = self.clone();
        blocking(move || {
            let handle = this
                .inner
                .worker
                .lock()
                .unwrap_or_else(|e| e.into_inner())
                .take();
            if let Some(handle) = handle {
                handle.join().map_err(|_| {
                    CoreError::new(ErrorCode::Internal, "workspace worker panicked")
                })?;
            }
            let mut s = this.inner.shared.lock();
            while !s.closed {
                s = this
                    .inner
                    .shared
                    .wake
                    .wait(s)
                    .unwrap_or_else(|e| e.into_inner());
            }
            Ok(())
        })
        .await
    }
    pub async fn search(&self, request: SearchRequest) -> Result<SymbolsResponse> {
        version(request.api_version)?;
        limit(request.limit)?;
        if request.query.len() > MAX_QUERY_BYTES || request.query.contains(char::from(0)) {
            return Err(invalid("query exceeds 512 bytes or contains NUL"));
        }
        self.query(move |this, db, revision| {
            this.symbols_response(
                db.search(&request.query, request.limit + 1)?,
                revision,
                request.limit,
            )
        })
        .await
    }
    pub async fn file_symbols(&self, request: FileSymbolsRequest) -> Result<SymbolsResponse> {
        version(request.api_version)?;
        limit(request.limit)?;
        self.query(move |this, db, revision| {
            let path = this.checked_file(&request.file)?;
            this.symbols_response(db.in_file(&path)?, revision, request.limit)
        })
        .await
    }
    pub async fn details(&self, request: DetailRequest) -> Result<DetailResponse> {
        version(request.api_version)?;
        self.query(move |this, db, revision| {
            let symbol = this.resolve_reference(db, &request.reference, revision)?;
            let metadata = db
                .in_file_with_metadata(&input_path(&symbol.reference.file)?)?
                .into_iter()
                .find(|r| {
                    r.symbol.name == symbol.name
                        && r.symbol.byte_range.start == symbol.byte_range.start
                        && r.symbol.byte_range.end == symbol.byte_range.end
                })
                .and_then(|r| r.metadata)
                .map(metadata_dto);
            let response = DetailResponse {
                api_version: API_VERSION,
                session_id: this.inner.id.clone(),
                revision,
                symbol,
                metadata,
            };
            jobs::ensure_result_size(&response)?;
            Ok(response)
        })
        .await
    }
    pub async fn inheritance(&self, request: InheritanceRequest) -> Result<InheritanceResponse> {
        version(request.api_version)?;
        limit(request.limit)?;
        self.query(move |this, db, revision| {
            let symbol = this.resolve_reference(db, &request.reference, revision)?;
            let mut seen = HashSet::from([symbol.name]);
            let mut pending: VecDeque<_> = symbol.bases.into_iter().map(|b| (b, 0)).collect();
            let mut bases = Vec::new();
            let mut truncated = false;
            while let Some((name, depth)) = pending.pop_front() {
                if !seen.insert(name.clone()) {
                    continue;
                }
                if bases.len() == request.limit {
                    truncated = true;
                    break;
                }
                bases.push(name.clone());
                if depth >= 63 {
                    truncated = true;
                    continue;
                }
                // Bounded lookup. Ambiguous names stop expansion instead of
                // guessing C++ ownership/type resolution.
                let candidates: Vec<_> = db
                    .prefix(&name, MAX_RESULTS + 1)?
                    .into_iter()
                    .filter(|r| r.symbol.name == name && this.visible(&r.file))
                    .collect();
                if candidates.len() == 1 {
                    for base in &candidates[0].symbol.bases {
                        if pending.len() >= MAX_RESULTS {
                            truncated = true;
                            break;
                        }
                        pending.push_back((base.clone(), depth + 1));
                    }
                }
            }
            let response = InheritanceResponse {
                api_version: API_VERSION,
                session_id: this.inner.id.clone(),
                revision,
                bases,
                truncated,
            };
            jobs::ensure_result_size(&response)?;
            Ok(response)
        })
        .await
    }
    async fn query<T, F>(&self, f: F) -> Result<T>
    where
        T: Send + 'static,
        F: FnOnce(&Self, &Db, u64) -> Result<T> + Send + 'static,
    {
        let permit = self
            .inner
            .queries
            .clone()
            .try_acquire_owned()
            .map_err(|_| CoreError::new(ErrorCode::Busy, "too many concurrent queries"))?;
        let this = self.clone();
        blocking(move || {
            let _permit = permit;
            this.ensure_open()?;
            let db = this.inner.db.read().unwrap_or_else(|e| e.into_inner());
            this.ensure_open()?;
            let db = db
                .as_ref()
                .ok_or_else(|| CoreError::new(ErrorCode::Closed, "session closed"))?;
            let revision = this.inner.shared.lock().revision;
            f(&this, db, revision)
        })
        .await
    }
    fn ensure_open(&self) -> Result<()> {
        if self.inner.shared.lock().closing {
            Err(CoreError::new(
                ErrorCode::Closed,
                "session closing or closed",
            ))
        } else {
            Ok(())
        }
    }
    fn checked_file(&self, file: &str) -> Result<PathBuf> {
        let path = input_path(file)?;
        if !index::is_header(&path) {
            return Err(CoreError::new(
                ErrorCode::ExcludedPath,
                "only non-generated .h/.hpp headers are eligible",
            ));
        }
        let resolved = index::resolve_path(&path)?.ok_or_else(|| {
            CoreError::new(ErrorCode::ExcludedPath, "excluded or redirected path")
        })?;
        if !self.inner.roots.iter().any(|r| resolved.starts_with(r)) {
            return Err(CoreError::new(
                ErrorCode::OutsideRoots,
                "path is outside workspace roots",
            ));
        }
        if !index::is_header(&resolved)
            || index::resolve_path(&resolved)?.as_ref() != Some(&resolved)
        {
            return Err(CoreError::new(
                ErrorCode::ExcludedPath,
                "canonical path is excluded",
            ));
        }
        Ok(resolved)
    }
    fn visible(&self, file: &str) -> bool {
        self.checked_file(&native_path(file)).is_ok()
    }
    fn symbols_response(
        &self,
        rows: Vec<StoredSymbol>,
        revision: u64,
        max: usize,
    ) -> Result<SymbolsResponse> {
        let mut symbols = Vec::new();
        let mut truncated = false;
        for row in rows {
            if !self.visible(&row.file) {
                continue;
            }
            if symbols.len() == max {
                truncated = true;
                break;
            }
            symbols.push(symbol_dto(row, &self.inner.id, revision)?);
        }
        let response = SymbolsResponse {
            api_version: API_VERSION,
            session_id: self.inner.id.clone(),
            revision,
            symbols,
            truncated,
        };
        jobs::ensure_result_size(&response)?;
        Ok(response)
    }
    fn resolve_reference(
        &self,
        db: &Db,
        reference: &SymbolReference,
        revision: u64,
    ) -> Result<SymbolDto> {
        if reference.session_id != self.inner.id || reference.revision != revision {
            return Err(CoreError::new(
                ErrorCode::StaleReference,
                "symbol reference belongs to another session or index revision",
            ));
        }
        if reference.symbol_key.len() != 64
            || !reference.symbol_key.bytes().all(|b| b.is_ascii_hexdigit())
        {
            return Err(invalid("invalid symbol key"));
        }
        let path = self.checked_file(&native_path(&reference.file))?;
        for row in db.in_file(&path)? {
            let dto = symbol_dto(row, &self.inner.id, revision)?;
            if dto.reference == *reference {
                return Ok(dto);
            }
        }
        Err(CoreError::new(
            ErrorCode::NotFound,
            "symbol declaration no longer exists",
        ))
    }
}

fn worker_loop(
    db: Database,
    shared: Arc<Shared>,
    roots: Vec<PathBuf>,
    session_id: String,
    lease: File,
) {
    struct Cleanup {
        db: Database,
        shared: Arc<Shared>,
        lease: Option<File>,
    }
    impl Drop for Cleanup {
        fn drop(&mut self) {
            self.db.write().unwrap_or_else(|e| e.into_inner()).take();
            self.lease.take();
            let mut s = self.shared.lock();
            s.closed = true;
            s.closing = true;
            self.shared.wake.notify_all();
        }
    }
    let _cleanup = Cleanup {
        db: db.clone(),
        shared: shared.clone(),
        lease: Some(lease),
    };
    loop {
        let (pending, token) = {
            let mut state = shared.lock();
            while state.queue.is_empty() && !state.closing {
                state = shared.wake.wait(state).unwrap_or_else(|e| e.into_inner());
            }
            let Some(pending) = state.queue.pop_front() else {
                break;
            };
            let job = state
                .jobs
                .get_mut(&pending.id)
                .expect("queued record exists");
            job.status.state = JobState::Running;
            job.status.started_at_ms = Some(jobs::now());
            (pending, job.token.clone())
        };
        let id = pending.id.clone();
        let index_job = !matches!(&pending.work, Work::Custom(_));
        let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            token.check()?;
            match pending.work {
                Work::Custom(task) => {
                    let revision = shared.lock().revision;
                    if let Some(record) = shared.lock().jobs.get_mut(&id) { record.status.revision = revision; }
                    task(JobContext { session_id: session_id.clone(), job_id: id.clone(), revision, roots: roots.iter().map(|p| ue_db::path_key(p)).collect(), cancellation: token.clone() })
                }
                work => {
                    let guard = db.write().unwrap_or_else(|e| e.into_inner());
                    let db = guard.as_ref().ok_or_else(|| CoreError::new(ErrorCode::Closed, "session closed"))?;
                    let revision = {
                        let mut s = shared.lock(); s.revision += 1; let revision = s.revision;
                        if let Some(record) = s.jobs.get_mut(&id) { record.status.revision = revision; } revision
                    };
                    let report = match work {
                        Work::Index => index::index_workspace_cancellable(db, &roots, &token.0),
                        Work::Files(files) => {
                            let mut report = index::IndexReport::default();
                            for file in files {
                                if token.is_cancelled() { break; }
                                match index::resolve_path(&file) {
                                    Ok(Some(path)) if roots.iter().any(|r| path.starts_with(r)) => {
                                        let part = index::index_file(db, &path);
                                        report.indexed += part.indexed; report.unchanged += part.unchanged; report.removed += part.removed; report.errors.extend(part.errors);
                                    }
                                    Ok(_) => report.errors.push("file became excluded or escaped roots".into()),
                                    Err(error) => report.errors.push(error.to_string()),
                                }
                            }
                            report
                        }
                        Work::Custom(_) => unreachable!(),
                    };
                    if let Ok(stats) = db.stats() { let mut s = shared.lock(); s.indexed_files = stats.files; s.indexed_symbols = stats.symbols; }
                    token.check()?;
                    let mut errors = report.errors;
                    let errors_truncated = errors.len() > 32;
                    errors.truncate(32);
                    let errors = errors.into_iter().map(|e| CoreError::new(ErrorCode::IndexFailed, e).message).collect();
                    let report = IndexReportDto { indexed: report.indexed, unchanged: report.unchanged, removed: report.removed, errors, errors_truncated };
                    Ok(serde_json::json!({ "apiVersion": API_VERSION, "sessionId": session_id, "revision": revision, "report": report }))
                }
            }
        })).unwrap_or_else(|_| Err(CoreError::new(ErrorCode::Internal, "job panicked")));
        shared.finish(&id, result, index_job);
    }
}

fn symbol_dto(row: StoredSymbol, session: &str, revision: u64) -> Result<SymbolDto> {
    let s = row.symbol;
    let mut dto = SymbolDto {
        reference: SymbolReference {
            session_id: session.into(),
            revision,
            file: row.file,
            symbol_key: String::new(),
        },
        name: s.name,
        kind: s.kind.as_str().into(),
        macro_name: s.macro_name,
        specifiers: s
            .specifiers
            .into_iter()
            .map(|s| SpecifierDto {
                key: s.key,
                value: s.value,
            })
            .collect(),
        type_name: s.type_name,
        bases: s.bases,
        line: s.line,
        byte_range: ByteRange {
            start: s.byte_range.start,
            end: s.byte_range.end,
        },
    };
    jobs::ensure_result_size(&dto)?;
    let mut fingerprint = dto.clone();
    fingerprint.reference.session_id.clear();
    fingerprint.reference.revision = 0;
    let bytes = serde_json::to_vec(&fingerprint)
        .map_err(|e| CoreError::new(ErrorCode::Internal, e.to_string()))?;
    dto.reference.symbol_key = format!("{:x}", Sha256::digest(bytes));
    Ok(dto)
}
fn version(v: u32) -> Result<()> {
    if v == API_VERSION {
        Ok(())
    } else {
        Err(invalid("unsupported apiVersion"))
    }
}
fn limit(v: usize) -> Result<()> {
    if (1..=MAX_RESULTS).contains(&v) {
        Ok(())
    } else {
        Err(invalid("limit must be 1..=200"))
    }
}
fn invalid(message: &str) -> CoreError {
    CoreError::new(ErrorCode::InvalidInput, message)
}
fn validate_job_id(id: &str) -> Result<()> {
    if id.len() <= 64 && uuid::Uuid::parse_str(id).is_ok() {
        Ok(())
    } else {
        Err(invalid("invalid job ID"))
    }
}
fn input_path(value: &str) -> Result<PathBuf> {
    if value.is_empty() || value.len() > MAX_PATH_BYTES || value.contains(char::from(0)) {
        return Err(invalid("invalid path length or NUL"));
    }
    let path = PathBuf::from(native_path(value));
    if !path.is_absolute() {
        return Err(invalid("paths must be absolute"));
    }
    Ok(path)
}
fn native_path(value: &str) -> String {
    #[cfg(windows)]
    {
        value.replace('/', std::path::MAIN_SEPARATOR_STR)
    }
    #[cfg(not(windows))]
    {
        value.into()
    }
}
fn reject_redirects(path: &Path) -> Result<()> {
    if path.components().any(|c| c == Component::ParentDir) {
        return Err(invalid("cache path must be canonical"));
    }
    for ancestor in path.ancestors() {
        match fs::symlink_metadata(ancestor) {
            Ok(meta) => {
                #[cfg(windows)]
                let redirect = {
                    use std::os::windows::fs::MetadataExt;
                    meta.file_attributes() & 0x400 != 0
                };
                #[cfg(not(windows))]
                let redirect = false;
                if redirect || meta.file_type().is_symlink() {
                    return Err(CoreError::new(
                        ErrorCode::ExcludedPath,
                        "cache path contains a symbolic link or reparse point",
                    ));
                }
            }
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => {}
            Err(e) => return Err(e.into()),
        }
    }
    Ok(())
}
async fn blocking<T: Send + 'static>(f: impl FnOnce() -> Result<T> + Send + 'static) -> Result<T> {
    tokio::task::spawn_blocking(f)
        .await
        .map_err(|e| CoreError::new(ErrorCode::Internal, e.to_string()))?
}

fn metadata_dto(m: ue_parser::SymbolMetadata) -> MetadataDto {
    MetadataDto {
        owner: m.owner,
        qualified_name: m.qualified_name,
        signature: m.signature,
        declaration: m.declaration,
        documentation: m.documentation,
        name_range: m.name_range.map(|r| ByteRange {
            start: r.start,
            end: r.end,
        }),
        declaration_range: m.declaration_range.map(|r| ByteRange {
            start: r.start,
            end: r.end,
        }),
    }
}
