//! Live references and adapter jobs; core owns the only disk writer.
use super::*;
use serde::Deserialize;
use serde_json::{json, Value};
use std::collections::VecDeque;
use std::sync::{
    atomic::{AtomicU64, Ordering},
    Weak,
};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};
const REFERENCES: usize = 1024;
const AI_RETAINED: usize = 32;
const AI_CONCURRENT: usize = 2;
const JOB_TTL: Duration = Duration::from_secs(300);
pub(super) fn memory_session_id() -> String {
    static NEXT: AtomicU64 = AtomicU64::new(0);
    format!(
        "live-{}-{}-{}",
        std::process::id(),
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap_or_default()
            .as_nanos(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    )
}
#[derive(Deserialize)]
#[serde(default, rename_all = "camelCase")]
pub(super) struct Options {
    pub adapter: String,
    pub engine_roots: Vec<String>,
    pub ai: AiOptions,
    pub style: StyleOptions,
}
impl Default for Options {
    fn default() -> Self {
        Self {
            adapter: "lsp".into(),
            engine_roots: Vec::new(),
            ai: AiOptions::default(),
            style: StyleOptions::default(),
        }
    }
}
#[derive(Default, Deserialize)]
#[serde(default)]
pub(super) struct AiOptions {
    enabled: bool,
    endpoint: Option<String>,
    model: Option<String>,
}
impl AiOptions {
    pub fn client(self) -> (Option<ue_ai::AiClient>, Option<String>) {
        if !self.enabled {
            return (None, None);
        }
        let mut config = ue_ai::AiConfig::default();
        if let Some(endpoint) = self.endpoint {
            config.endpoint = endpoint;
        }
        if let Some(model) = self.model {
            config.model = model;
        }
        match ue_ai::AiClient::new(config) {
            Ok(client) => (Some(client), None),
            Err(error) => (None, Some(error.to_string())),
        }
    }
}
#[derive(Clone)]
enum Origin {
    Live {
        uri: Url,
        revision: Weak<Buffer>,
        version: i32,
    },
    Disk(ue_core::SymbolReference),
}
#[derive(Clone)]
struct Reference {
    symbol: Symbol,
    origin: Origin,
    session: String,
}
#[derive(Default)]
pub(super) struct References {
    next: u64,
    entries: VecDeque<(String, Reference)>,
}
impl References {
    fn insert(&mut self, session: &str, mut reference: Reference) -> Symbol {
        self.next += 1;
        let id = format!("{session}:symbol:{}", self.next);
        reference.symbol.id = id.clone();
        let symbol = reference.symbol.clone();
        self.entries.push_back((id, reference));
        while self.entries.len() > REFERENCES {
            self.entries.pop_front();
        }
        symbol
    }
    fn get(&self, id: &str) -> Option<Reference> {
        self.entries
            .iter()
            .find(|(key, _)| key == id)
            .map(|(_, item)| item.clone())
    }
}
struct AiRecord {
    job: Job,
    task: Option<JoinHandle<()>>,
    updated: Instant,
}
#[derive(Default)]
pub(super) struct AiJobs {
    next: u64,
    entries: BTreeMap<String, AiRecord>,
}
impl AiJobs {
    fn prune(&mut self) {
        self.entries
            .retain(|_, item| !item.job.state.is_terminal() || item.updated.elapsed() < JOB_TTL);
        while self.entries.len() >= AI_RETAINED {
            let oldest = self
                .entries
                .iter()
                .filter(|(_, r)| r.job.state.is_terminal())
                .min_by_key(|(_, r)| r.updated)
                .map(|(id, _)| id.clone());
            if let Some(id) = oldest {
                self.entries.remove(&id);
            } else {
                break;
            }
        }
    }
    fn cancel(&mut self, id: &str) -> bool {
        let Some(record) = self.entries.get_mut(id) else {
            return false;
        };
        if record.job.state.is_terminal() {
            return false;
        }
        if let Some(task) = record.task.take() {
            task.abort();
        }
        record.job.state = ue_core::JobState::Cancelled;
        record.job.result = None;
        record.updated = Instant::now();
        true
    }
    pub fn cancel_all(&mut self) {
        for id in self.entries.keys().cloned().collect::<Vec<_>>() {
            self.cancel(&id);
        }
    }
}
impl Drop for AiJobs {
    fn drop(&mut self) {
        self.cancel_all();
    }
}
pub(super) fn core_error(error: ue_core::CoreError) -> Error {
    let mut mapped = Error::invalid_params(error.message.clone());
    if !matches!(
        error.code,
        ue_core::ErrorCode::InvalidInput
            | ue_core::ErrorCode::OutsideRoots
            | ue_core::ErrorCode::ExcludedPath
    ) {
        mapped = Error {
            code: tower_lsp::jsonrpc::ErrorCode::ServerError(-32001),
            message: error.message.clone().into(),
            data: None,
        };
    }
    mapped.data = serde_json::to_value(error).ok();
    mapped
}
fn unavailable(message: impl Into<String>) -> Error {
    Error {
        code: tower_lsp::jsonrpc::ErrorCode::ServerError(-32002),
        message: message.into().into(),
        data: None,
    }
}
impl Backend {
    fn active_core(&self) -> Result<Arc<ue_core::WorkspaceSession>> {
        let state = self.state();
        if state.stopping {
            return Err(unavailable("Session is stopping"));
        }
        state.core.clone().ok_or_else(|| {
            unavailable(
                state
                    .message
                    .clone()
                    .unwrap_or_else(|| "No disk workspace; open documents remain available".into()),
            )
        })
    }
    pub async fn penguin_status(&self, _: EmptyParams) -> Result<Status> {
        let (core, session_id, stopping, roots, message, snapshot) = {
            let state = self.state();
            (
                state.core.clone(),
                state.session_id.clone(),
                state.stopping,
                state.roots.iter().map(|p| portable_path(p)).collect(),
                state.message.clone(),
                state.buffers.clone(),
            )
        };
        let (files, symbols) = spawn_blocking(move || {
            (
                snapshot.len(),
                snapshot
                    .values()
                    .map(|b| b.document().parsed.symbols.len())
                    .sum(),
            )
        })
        .await
        .map_err(|_| Error::internal_error())?;
        let mut result = Status {
            protocol_version: 1,
            session_id,
            state: if stopping { "stopping" } else { "ready" },
            roots,
            files,
            symbols,
            message,
        };
        if let Some(core) = core {
            let status = core.status();
            result.files = status.indexed_files;
            result.symbols = status.indexed_symbols;
            if !stopping && status.running_jobs + status.queued_jobs > 0 {
                result.state = "indexing";
            }
        } else if result.message.is_some() && !stopping {
            result.state = "unavailable";
        }
        Ok(result)
    }

    async fn search_references(&self, query: String, limit: usize) -> Result<Vec<Reference>> {
        if query.len() > ue_core::MAX_QUERY_BYTES || query.contains(char::from(0)) {
            return Err(Error::invalid_params(
                "query exceeds 512 bytes or contains NUL",
            ));
        }
        let (core, session, snapshot) = {
            let state = self.state();
            if state.stopping {
                return Err(unavailable("Session is stopping"));
            }
            (
                state.core.clone(),
                state.session_id.clone(),
                Snapshot {
                    db: state.db.clone(),
                    root_aliases: state.root_aliases.clone(),
                    buffers: state.buffers.clone(),
                },
            )
        };
        let disk = if let Some(core) = core {
            tokio::time::timeout(
                Duration::from_millis(750),
                core.search(ue_core::SearchRequest {
                    api_version: 1,
                    query: query.clone(),
                    limit: ue_core::MAX_RESULTS,
                }),
            )
            .await
            .map_err(|_| unavailable("Workspace query busy; retry after indexing"))?
            .map_err(core_error)?
            .symbols
        } else {
            Vec::new()
        };
        spawn_blocking(move || {
            let mut results = Vec::new();
            let mut open_files = HashSet::new();
            let query = query.to_ascii_lowercase();
            let mut parser = ue_parser::UnrealParser::new().ok();
            for (uri, revision) in snapshot.buffers {
                let file = uri.to_file_path().ok();
                if let Some(file) = &file {
                    open_files.insert(identity(file));
                    open_files.insert(identity(&resolved_path(file)));
                }
                let file = file
                    .map(|p| portable_path(&p))
                    .unwrap_or_else(|| uri.to_string());
                let rich = parser
                    .as_mut()
                    .map(|p| p.parse_with_metadata(&revision.source));
                let symbols = rich
                    .as_ref()
                    .map(|r| &r.symbols)
                    .unwrap_or(&revision.document().parsed.symbols);
                for (index, symbol) in symbols.iter().enumerate() {
                    if results.len() >= limit {
                        break;
                    }
                    if !symbol.name.to_ascii_lowercase().contains(&query) {
                        continue;
                    }
                    let metadata = rich.as_ref().and_then(|r| r.metadata.get(index));
                    results.push(Reference {
                        symbol: Symbol::live(symbol, metadata, file.clone()),
                        origin: Origin::Live {
                            uri: uri.clone(),
                            revision: Arc::downgrade(&revision),
                            version: revision.version,
                        },
                        session: session.clone(),
                    });
                }
            }
            for symbol in disk {
                if results.len() >= limit {
                    break;
                }
                let file = client_path(Path::new(&symbol.reference.file), &snapshot.root_aliases);
                if open_files.contains(&identity(Path::new(&symbol.reference.file)))
                    || open_files.contains(&identity(Path::new(&file)))
                {
                    continue;
                }
                let origin = Origin::Disk(symbol.reference.clone());
                results.push(Reference {
                    symbol: Symbol::disk(symbol, file),
                    origin,
                    session: session.clone(),
                });
            }
            results
        })
        .await
        .map_err(|_| Error::internal_error())
    }
    fn register(&self, references: Vec<Reference>) -> Vec<Symbol> {
        let mut state = self.state();
        let session = state.session_id.clone();
        if state.stopping {
            return Vec::new();
        }
        references
            .into_iter()
            .filter(|r| r.session == session)
            .map(|r| state.references.insert(&session, r))
            .collect()
    }
    pub async fn penguin_symbols(&self, params: SearchParams) -> Result<Vec<Symbol>> {
        let limit = params.limit.unwrap_or(50).min(ue_core::MAX_RESULTS);
        if limit == 0 {
            return Err(Error::invalid_params("limit must be positive"));
        }
        Ok(self.register(self.search_references(params.query, limit).await?))
    }
    pub async fn penguin_symbol(&self, params: IdParams) -> Result<Option<Symbol>> {
        let reference = {
            let state = self.state();
            if state.stopping {
                return Ok(None);
            }
            state
                .references
                .get(&params.id)
                .filter(|r| r.session == state.session_id)
        };
        let Some(reference) = reference else {
            return Ok(None);
        };
        match &reference.origin {
            Origin::Live {
                uri,
                revision,
                version,
            } => {
                let state = self.state();
                Ok(revision
                    .upgrade()
                    .filter(|old| {
                        state.buffers.get(uri).is_some_and(|current| {
                            current.version == *version && Arc::ptr_eq(old, current)
                        })
                    })
                    .map(|_| reference.symbol))
            }
            Origin::Disk(disk) => {
                let snapshot = self.snapshot();
                let file = disk.file.clone();
                let hidden = spawn_blocking(move || {
                    snapshot
                        .buffers
                        .keys()
                        .filter_map(|uri| uri.to_file_path().ok())
                        .any(|path| {
                            identity(&path) == identity(Path::new(&file))
                                || identity(&resolved_path(&path)) == identity(Path::new(&file))
                        })
                })
                .await
                .map_err(|_| Error::internal_error())?;
                if hidden {
                    return Ok(None);
                }
                let core = self.active_core()?;
                match tokio::time::timeout(
                    Duration::from_millis(750),
                    core.details(ue_core::DetailRequest {
                        api_version: 1,
                        reference: disk.clone(),
                    }),
                )
                .await
                .map_err(|_| unavailable("Workspace query busy; retry after indexing"))?
                {
                    Ok(details) => {
                        let mut symbol = Symbol::disk(details.symbol, reference.symbol.file);
                        if let Some(metadata) = details.metadata {
                            symbol.owner = metadata.owner;
                            symbol.qualified_name = metadata.qualified_name;
                            symbol.signature = metadata.signature;
                            symbol.documentation = metadata.documentation;
                        }
                        symbol.id = params.id;
                        Ok(Some(symbol))
                    }
                    Err(error)
                        if matches!(
                            error.code,
                            ue_core::ErrorCode::NotFound | ue_core::ErrorCode::StaleReference
                        ) =>
                    {
                        Ok(None)
                    }
                    Err(error) => Err(core_error(error)),
                }
            }
        }
    }
    pub async fn penguin_inheritance(&self, params: NameParams) -> Result<Inheritance> {
        let matches = self
            .search_references(params.name.clone(), ue_core::MAX_RESULTS)
            .await?;
        let exact: Vec<_> = matches
            .iter()
            .filter(|r| r.symbol.name == params.name)
            .collect();
        let bases = if exact.len() == 1 {
            // The core can expand disk ancestry. Any live overlay means its
            // disk-only transitive view could be stale, so use proven direct bases.
            let no_overlays = self.state().buffers.is_empty();
            if let Origin::Disk(reference) = &exact[0].origin {
                if no_overlays {
                    tokio::time::timeout(
                        Duration::from_millis(750),
                        self.active_core()?
                            .inheritance(ue_core::InheritanceRequest {
                                api_version: 1,
                                reference: reference.clone(),
                                limit: ue_core::MAX_RESULTS,
                            }),
                    )
                    .await
                    .map_err(|_| unavailable("Workspace query busy; retry after indexing"))?
                    .map_err(core_error)?
                    .bases
                } else {
                    exact[0].symbol.bases.clone()
                }
            } else {
                exact[0].symbol.bases.clone()
            }
        } else {
            Vec::new()
        };
        let derived = self
            .search_references(String::new(), ue_core::MAX_RESULTS)
            .await?
            .into_iter()
            .filter(|r| r.symbol.bases.contains(&params.name))
            .collect();
        Ok(Inheritance {
            bases,
            derived: self.register(derived),
        })
    }
    pub async fn penguin_reindex(&self, _: EmptyParams) -> Result<Job> {
        self.active_core()?
            .reindex()
            .map(Into::into)
            .map_err(core_error)
    }
    pub async fn penguin_job(&self, params: IdParams) -> Result<Option<Job>> {
        let core = {
            let mut state = self.state();
            state.ai_jobs.prune();
            if let Some(record) = state.ai_jobs.entries.get(&params.id) {
                return Ok(Some(record.job.clone()));
            }
            state.core.clone()
        };
        let Some(core) = core else {
            return Ok(None);
        };
        match core.job_status(&params.id) {
            Ok(job) => Ok(Some(job.into())),
            Err(error)
                if matches!(
                    error.code,
                    ue_core::ErrorCode::NotFound | ue_core::ErrorCode::InvalidInput
                ) =>
            {
                Ok(None)
            }
            Err(error) => Err(core_error(error)),
        }
    }
    pub async fn penguin_cancel_job(&self, params: IdParams) -> Result<CancelResult> {
        let core = {
            let mut state = self.state();
            if state.ai_jobs.entries.contains_key(&params.id) {
                return Ok(CancelResult {
                    cancelled: state.ai_jobs.cancel(&params.id),
                });
            }
            state.core.clone()
        };
        let cancelled = core.is_some_and(|core| {
            core.cancel_job(&params.id)
                .is_ok_and(|job| !job.state.is_terminal())
        });
        Ok(CancelResult { cancelled })
    }

    pub async fn penguin_ai(&self, request: ue_ai::AiRequest) -> Result<Job> {
        if request.source.len() > 64 * 1024 || request.instruction.len() > 8 * 1024 {
            return Err(Error::invalid_params(
                "AI source or instruction exceeds allowed size",
            ));
        }
        let mut state = self.state();
        if state.stopping {
            return Err(unavailable("Session is stopping"));
        }
        let Some(client) = state.ai.clone() else {
            return Err(unavailable(state.ai_error.clone().unwrap_or_else(|| {
                "AI is disabled; explicitly enable local AI in initializationOptions.penguin.ai"
                    .into()
            })));
        };
        if let Some(core) = state.core.clone() {
            state
                .core_ai_jobs
                .retain(|id| core.job_status(id).is_ok_and(|s| !s.state.is_terminal()));
            if state.core_ai_jobs.len() >= AI_CONCURRENT {
                return Err(unavailable("AI concurrency limit reached"));
            }
            let runtime = tokio::runtime::Handle::current();
            let job = core.submit_job("ai", move |context| {
                runtime.block_on(async move {
                    tokio::select! {
                        result = client.generate(request) => result.map(|r| json!({"text":r.preview,"model":r.model})).map_err(|error| ue_core::CoreError::new(ue_core::ErrorCode::Internal, error.to_string())),
                        _ = async { loop { if context.cancellation.is_cancelled() { break; } tokio::time::sleep(Duration::from_millis(20)).await; } } => Err(ue_core::CoreError::new(ue_core::ErrorCode::Cancelled, "AI job cancelled")),
                    }
                })
            }).map_err(core_error)?;
            state.core_ai_jobs.insert(job.job_id.clone());
            return Ok(job.into());
        }
        // No-root clients need no fake disk workspace for explicit buffer AI.
        state.ai_jobs.prune();
        if state
            .ai_jobs
            .entries
            .values()
            .filter(|r| !r.job.state.is_terminal())
            .count()
            >= AI_CONCURRENT
        {
            return Err(unavailable("AI concurrency limit reached"));
        }
        state.ai_jobs.next += 1;
        let id = format!("{}:ai:{}", state.session_id, state.ai_jobs.next);
        let job = Job {
            id: id.clone(),
            session_id: state.session_id.clone(),
            kind: "ai".into(),
            state: ue_core::JobState::Running,
            result: None,
            error: None,
        };
        let weak = Arc::downgrade(&self.state);
        let task_id = id.clone();
        let task = tokio::spawn(async move {
            let outcome = client.generate(request).await;
            let Some(state) = weak.upgrade() else {
                return;
            };
            let mut state = state.lock().unwrap_or_else(|e| e.into_inner());
            if state.stopping {
                return;
            }
            if let Some(record) = state.ai_jobs.entries.get_mut(&task_id) {
                if record.job.state.is_terminal() {
                    return;
                }
                record.updated = Instant::now();
                match outcome {
                    Ok(response) => {
                        record.job.state = ue_core::JobState::Succeeded;
                        record.job.result =
                            Some(json!({"text":response.preview,"model":response.model}));
                    }
                    Err(error) => {
                        record.job.state = ue_core::JobState::Failed;
                        record.job.error = Some(error.to_string());
                    }
                }
            }
        });
        state.ai_jobs.entries.insert(
            id,
            AiRecord {
                job: job.clone(),
                task: Some(task),
                updated: Instant::now(),
            },
        );
        Ok(job)
    }
    pub(super) fn schedule_diagnostics(&self, uri: Url, revision: Arc<Buffer>) {
        let mut state = self.state();
        if let Some(task) = state.diagnostics.remove(&uri) {
            task.abort();
        }
        if state.stopping || !state.style.enabled {
            return;
        }
        let options = state.style.clone();
        let epoch = state.diagnostic_epoch;
        let session = state.session_id.clone();
        let weak = Arc::downgrade(&self.state);
        let gate = self.diagnostic_gate.clone();
        let client = self.client.clone();
        let task_uri = uri.clone();
        let task = tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(100)).await;
            let parsed_revision = revision.clone();
            let Ok(diagnostics) = spawn_blocking(move || {
                crate::diagnostics::check(parsed_revision.document(), &options)
            })
            .await
            else {
                return;
            };
            let _gate = gate.lock().await;
            let Some(state) = weak.upgrade() else {
                return;
            };
            let valid = {
                let state = state.lock().unwrap_or_else(|e| e.into_inner());
                diagnostic_is_current(&state, &task_uri, &revision, &session, epoch)
            };
            // Updates and clears share this gate: guard and publish are atomic
            // relative to installing a new authoritative buffer revision.
            if valid {
                client
                    .publish_diagnostics(task_uri, diagnostics, Some(revision.version))
                    .await;
            }
        });
        state.diagnostics.insert(uri, task);
    }
    pub(super) async fn configure_style(&self, settings: Value) {
        let Some(value) = settings.get("penguin").unwrap_or(&settings).get("style") else {
            return;
        };
        let Ok(options) = serde_json::from_value::<StyleOptions>(value.clone()) else {
            return;
        };
        let _gate = self.diagnostic_gate.lock().await;
        let buffers = {
            let mut state = self.state();
            state.style = options;
            state.diagnostic_epoch += 1;
            for (_, task) in std::mem::take(&mut state.diagnostics) {
                task.abort();
            }
            state.buffers.clone()
        };
        for (uri, revision) in buffers {
            self.client
                .publish_diagnostics(uri.clone(), Vec::new(), Some(revision.version))
                .await;
            self.schedule_diagnostics(uri, revision);
        }
    }
}

fn diagnostic_is_current(
    state: &State,
    uri: &Url,
    revision: &Arc<Buffer>,
    session: &str,
    epoch: u64,
) -> bool {
    !state.stopping
        && state.style.enabled
        && state.diagnostic_epoch == epoch
        && state.session_id == session
        && state
            .buffers
            .get(uri)
            .is_some_and(|current| Arc::ptr_eq(current, revision))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn diagnostic_guard_rejects_late_revision_close_reopen_disable_and_old_session() {
        let uri = Url::parse("untitled:Race.h").unwrap();
        let old = Arc::new(Buffer {
            version: 1,
            source: String::new(),
            parsed: OnceLock::new(),
        });
        let mut state = State {
            session_id: "session".into(),
            ..State::default()
        };
        state.buffers.insert(uri.clone(), old.clone());
        assert!(diagnostic_is_current(&state, &uri, &old, "session", 0));
        // Same URI/version after reopen is still a different immutable revision.
        state.buffers.insert(
            uri.clone(),
            Arc::new(Buffer {
                version: 1,
                source: String::new(),
                parsed: OnceLock::new(),
            }),
        );
        assert!(!diagnostic_is_current(&state, &uri, &old, "session", 0));
        let latest = state.buffers[&uri].clone();
        assert!(!diagnostic_is_current(
            &state,
            &uri,
            &latest,
            "old-session",
            0
        ));
        state.diagnostic_epoch = 1;
        assert!(!diagnostic_is_current(&state, &uri, &latest, "session", 0));
        state.style.enabled = false;
        assert!(!diagnostic_is_current(&state, &uri, &latest, "session", 1));
        state.style.enabled = true;
        state.buffers.clear();
        assert!(!diagnostic_is_current(&state, &uri, &latest, "session", 1));
    }
    #[tokio::test]
    async fn ai_memory_jobs_expire_are_bounded_and_cancel_without_late_results() {
        let mut jobs = AiJobs::default();
        for index in 0..100 {
            jobs.prune();
            let id = index.to_string();
            jobs.entries.insert(
                id.clone(),
                AiRecord {
                    job: Job {
                        id,
                        session_id: "test".into(),
                        kind: "ai".into(),
                        state: ue_core::JobState::Succeeded,
                        result: Some(json!({"text":"preview"})),
                        error: None,
                    },
                    task: None,
                    updated: Instant::now(),
                },
            );
        }
        assert!(jobs.entries.len() <= AI_RETAINED);
        for record in jobs.entries.values_mut() {
            record.updated = Instant::now() - JOB_TTL;
        }
        jobs.prune();
        assert!(jobs.entries.is_empty());
        jobs.entries.insert(
            "running".into(),
            AiRecord {
                job: Job {
                    id: "running".into(),
                    session_id: "test".into(),
                    kind: "ai".into(),
                    state: ue_core::JobState::Running,
                    result: None,
                    error: None,
                },
                task: Some(tokio::spawn(std::future::pending())),
                updated: Instant::now(),
            },
        );
        assert!(jobs.cancel("running"));
        assert!(!jobs.cancel("running"));
        assert_eq!(
            jobs.entries["running"].job.state,
            ue_core::JobState::Cancelled
        );
        assert!(jobs.entries["running"].job.result.is_none());
    }
}
