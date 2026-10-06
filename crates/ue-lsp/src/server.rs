//! LSP lifecycle, open-buffer overlays and bounded workspace lookups.
//! Parsing, features and DB/filesystem access run on blocking workers; index
//! writes run through one FIFO worker. Requests never load arbitrary URI paths.

use std::collections::{BTreeMap, HashSet};
use std::path::{Component, Path, PathBuf};
use std::sync::{Arc, Mutex, OnceLock};

use tokio::task::{spawn_blocking, JoinHandle};
use tower_lsp::jsonrpc::{Error, Result};
use tower_lsp::lsp_types::*;
use tower_lsp::{Client, ClientSocket, LanguageServer, LspService};
use ue_db::{path_key, Db, StoredSymbol};
use ue_parser::UnrealSymbol;

use crate::extensions::*;
#[cfg(test)]
use crate::index;
use crate::{docs::Document, features};

#[path = "extension_handlers.rs"]
mod extension_handlers;

/// One registration point used by the shipped stdio process and transport tests.
pub fn service() -> (LspService<Backend>, ClientSocket) {
    LspService::build(Backend::new)
        .custom_method("penguin/status", Backend::penguin_status)
        .custom_method("penguin/symbols", Backend::penguin_symbols)
        .custom_method("penguin/symbol", Backend::penguin_symbol)
        .custom_method("penguin/inheritance", Backend::penguin_inheritance)
        .custom_method("penguin/reindex", Backend::penguin_reindex)
        .custom_method("penguin/job", Backend::penguin_job)
        .custom_method("penguin/cancelJob", Backend::penguin_cancel_job)
        .custom_method("penguin/ai", Backend::penguin_ai)
        .finish()
}

const RESULT_LIMIT: usize = 200;
// Overfetch helps when open buffers suppress disk rows. SQL stays bounded even
// for an empty prefix; exceptionally large candidate sets are truncated.
const QUERY_LIMIT: usize = 1024;

/// An immutable revision: an old parse never publishes back into the map, so
/// it cannot replace a newer edit or resurrect a closed/reopened document.
struct Buffer {
    version: i32,
    source: String,
    parsed: OnceLock<Document>,
}

impl Buffer {
    fn document(&self) -> &Document {
        // Blocking workers only. Concurrent queries share the same parse.
        self.parsed
            .get_or_init(|| Document::parse(self.source.clone()))
    }
}

#[derive(Default)]
struct State {
    roots: Vec<PathBuf>,
    root_aliases: Vec<(PathBuf, PathBuf)>,
    db: Option<Db>,
    buffers: BTreeMap<Url, Arc<Buffer>>,
    indexer: Option<Indexer>,
    initialized: bool,
    stopping: bool,
    session_id: String,
    core: Option<Arc<ue_core::WorkspaceSession>>,
    message: Option<String>,
    style: StyleOptions,
    diagnostic_epoch: u64,
    diagnostics: BTreeMap<Url, JoinHandle<()>>,
    references: extension_handlers::References,
    ai: Option<ue_ai::AiClient>,
    ai_error: Option<String>,
    ai_jobs: extension_handlers::AiJobs,
    core_ai_jobs: HashSet<String>,
}

// The core owns the only serial writer/queue. This flag records initialized.
type Indexer = ();

struct Snapshot {
    db: Option<Db>,
    root_aliases: Vec<(PathBuf, PathBuf)>,
    buffers: BTreeMap<Url, Arc<Buffer>>,
}

enum Query<'a> {
    Prefix(&'a str),
    Exact(&'a str),
    Search(&'a str),
}

impl Query<'_> {
    fn matches(&self, name: &str) -> bool {
        match self {
            Self::Exact(query) => name == *query,
            Self::Prefix(query) => name
                .to_ascii_lowercase()
                .starts_with(&query.to_ascii_lowercase()),
            Self::Search(query) => name
                .to_ascii_lowercase()
                .contains(&query.to_ascii_lowercase()),
        }
    }
}

impl Snapshot {
    /// Remove ALL disk declarations belonging to an open file, including
    /// declarations deleted from that buffer; then overlay live parsed symbols.
    fn candidates(
        &self,
        query: Query<'_>,
    ) -> (Vec<StoredSymbol>, Vec<UnrealSymbol>, Option<String>) {
        let mut indexed = Vec::new();
        let mut symbols = Vec::new();
        let mut open_files = HashSet::new();
        for (uri, buffer) in &self.buffers {
            let file = uri.to_file_path().ok();
            if let Some(file) = &file {
                open_files.insert(identity(file));
                open_files.insert(identity(&resolved_path(file)));
            }
            for symbol in &buffer.document().parsed.symbols {
                if query.matches(&symbol.name) {
                    symbols.push(symbol.clone());
                    if let Some(file) = &file {
                        indexed.push(StoredSymbol {
                            file: path_key(file),
                            symbol: symbol.clone(),
                        });
                    }
                }
            }
        }
        let rows = match &self.db {
            Some(db) => match &query {
                Query::Prefix(prefix) | Query::Exact(prefix) => db.prefix(prefix, QUERY_LIMIT),
                Query::Search(query) => db.search(query, QUERY_LIMIT),
            },
            None => return (indexed, symbols, None),
        };
        match rows {
            Ok(rows) => {
                for mut row in rows {
                    let client_file = client_path(Path::new(&row.file), &self.root_aliases);
                    if !open_files.contains(&identity(Path::new(&row.file)))
                        && !open_files.contains(&identity(Path::new(&client_file)))
                        && query.matches(&row.symbol.name)
                    {
                        symbols.push(row.symbol.clone());
                        // Navigation needs a normal OS path, not a URI or a
                        // slash-normalized Windows verbatim prefix.
                        row.file = client_file;
                        indexed.push(row);
                    }
                }
                (indexed, symbols, None)
            }
            Err(error) => (
                indexed,
                symbols,
                Some(format!("Workspace query failed: {error}")),
            ),
        }
    }
}

/// tower-lsp 0.20 backend, constructed by `LspService::new(Backend::new)`.
pub struct Backend {
    client: Client,
    state: Arc<Mutex<State>>,
    diagnostic_gate: Arc<tokio::sync::Mutex<()>>,
}

impl Backend {
    pub fn new(client: Client) -> Self {
        Self {
            client,
            state: Arc::new(Mutex::new(State {
                session_id: extension_handlers::memory_session_id(),
                ..State::default()
            })),
            diagnostic_gate: Arc::new(tokio::sync::Mutex::new(())),
        }
    }

    fn state(&self) -> std::sync::MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|error| error.into_inner())
    }

    fn snapshot(&self) -> Snapshot {
        let state = self.state();
        Snapshot {
            db: state.db.clone(),
            root_aliases: state.root_aliases.clone(),
            buffers: state.buffers.clone(),
        }
    }

    async fn warning(&self, warning: Option<String>) {
        if let Some(warning) = warning {
            self.client.log_message(MessageType::WARNING, warning).await;
        }
    }

    async fn parse_revision(&self, revision: Arc<Buffer>) {
        if let Err(error) = spawn_blocking(move || {
            revision.document();
        })
        .await
        {
            self.warning(Some(format!("Document parse failed: {error}")))
                .await;
        }
    }

    async fn queue_files(&self, uris: impl IntoIterator<Item = Url>) {
        let (core, paths) = {
            let state = self.state();
            if state.stopping {
                return;
            }
            let paths = uris
                .into_iter()
                .filter_map(|uri| uri.to_file_path().ok())
                .filter(|path| {
                    within_roots(path, &state.roots)
                        || state
                            .root_aliases
                            .iter()
                            .any(|(_, alias)| within_roots(path, std::slice::from_ref(alias)))
                })
                .map(|path| path.to_string_lossy().into_owned())
                .collect::<Vec<_>>();
            (state.core.clone(), paths)
        };
        if let Some(core) = core {
            // Invalid/excluded paths must not poison valid siblings in a watch batch.
            // Core performs physical containment checks and serializes all mutations.
            for file in paths.into_iter().take(ue_core::MAX_CHANGED_FILES) {
                if let Err(error) = core
                    .files_changed(ue_core::FileChangesRequest {
                        api_version: 1,
                        files: vec![file],
                    })
                    .await
                {
                    self.warning(Some(format!("Index notification skipped: {error}")))
                        .await;
                }
            }
        }
    }

    async fn run_request<T: Send + 'static>(
        &self,
        work: impl FnOnce(Snapshot) -> (T, Option<String>) + Send + 'static,
    ) -> Result<T> {
        let snapshot = self.snapshot();
        let (result, warning) = spawn_blocking(move || work(snapshot))
            .await
            .map_err(|_| Error::internal_error())?;
        self.warning(warning).await;
        Ok(result)
    }
}

/// Canonical paths match the indexer. Deleted files use the closest existing
/// ancestor. Call only on blocking workers.
fn resolved_path(path: &Path) -> PathBuf {
    for ancestor in path.ancestors() {
        if let Ok(base) = std::fs::canonicalize(ancestor) {
            if let Ok(suffix) = path.strip_prefix(ancestor) {
                return if suffix.as_os_str().is_empty() {
                    base
                } else {
                    base.join(suffix)
                };
            }
        }
    }
    path.to_path_buf()
}

fn portable_path(path: &Path) -> String {
    let key = path_key(path);
    #[cfg(windows)]
    {
        // canonicalize uses extended-length paths; LSP normally does not.
        let key = if let Some(rest) = key.strip_prefix("//?/UNC/") {
            format!("//{rest}")
        } else {
            key.strip_prefix("//?/").unwrap_or(&key).to_string()
        };
        key
    }
    #[cfg(not(windows))]
    {
        key
    }
}

fn identity(path: &Path) -> String {
    let path = portable_path(path);
    if cfg!(windows) {
        path.to_ascii_lowercase()
    } else {
        path
    }
}

fn within_roots(path: &Path, roots: &[PathBuf]) -> bool {
    if !path.is_absolute()
        || path
            .components()
            .any(|part| matches!(part, Component::ParentDir))
    {
        return false;
    }
    let path = identity(path);
    roots.iter().any(|root| {
        let root = identity(root);
        let root = root.trim_end_matches('/');
        path == root
            || path
                .strip_prefix(root)
                .is_some_and(|tail| tail.starts_with('/'))
    })
}

fn client_path(path: &Path, aliases: &[(PathBuf, PathBuf)]) -> String {
    let spelling = portable_path(path);
    let key = identity(path);
    for (canonical, requested) in aliases {
        let root = identity(canonical);
        if let Some(suffix) = key.strip_prefix(root.trim_end_matches('/')) {
            if suffix.starts_with('/') {
                // Preserve client root spelling (including Windows 8.3 aliases)
                // without lowercasing case-sensitive components in the suffix.
                let suffix = &spelling[spelling.len() - suffix.len() + 1..];
                return path_key(&requested.join(suffix));
            }
        }
    }
    spelling
}

struct PreparedWorkspace {
    roots: Vec<PathBuf>,
    aliases: Vec<(PathBuf, PathBuf)>,
    warnings: Vec<String>,
}

fn prepare_workspace(requested: Vec<PathBuf>) -> PreparedWorkspace {
    let mut warnings = Vec::new();
    let mut roots = Vec::new();
    let mut aliases = Vec::new();
    for root in requested {
        match std::fs::canonicalize(&root) {
            Ok(canonical) if canonical.is_dir() => {
                aliases.push((canonical.clone(), root));
                if !roots.contains(&canonical) {
                    roots.push(canonical);
                }
            }
            Ok(_) => warnings.push(format!(
                "Workspace root is not a directory: {}",
                root.display()
            )),
            Err(error) => warnings.push(format!(
                "Cannot use workspace root {}: {error}",
                root.display()
            )),
        }
    }
    if roots.is_empty() {
        warnings.push("No local workspace root; serving open documents in memory only.".into());
    }
    PreparedWorkspace {
        roots,
        aliases,
        warnings,
    }
}

#[tower_lsp::async_trait]
impl LanguageServer for Backend {
    async fn initialize(&self, params: InitializeParams) -> Result<InitializeResult> {
        let mut roots: Vec<PathBuf> = params
            .workspace_folders
            .unwrap_or_default()
            .into_iter()
            .filter_map(|folder| folder.uri.to_file_path().ok())
            .collect();
        if roots.is_empty() {
            #[allow(deprecated)]
            if let Some(root) = params.root_uri.and_then(|uri| uri.to_file_path().ok()) {
                roots.push(root);
            }
        }
        let options: extension_handlers::Options = serde_json::from_value(
            params
                .initialization_options
                .as_ref()
                .and_then(|v| v.get("penguin"))
                .cloned()
                .unwrap_or_else(|| serde_json::json!({})),
        )
        .map_err(|error| Error::invalid_params(error.to_string()))?;
        for root in &options.engine_roots {
            if !Path::new(root).is_absolute() {
                return Err(Error::invalid_params(
                    "engineRoots must be absolute directories",
                ));
            }
            roots.push(PathBuf::from(root));
        }
        if roots.len() > ue_core::MAX_ROOTS {
            return Err(Error::invalid_params(
                "At most 16 workspace and engine roots are supported",
            ));
        }
        let PreparedWorkspace {
            roots,
            aliases,
            mut warnings,
        } = spawn_blocking(move || prepare_workspace(roots))
            .await
            .map_err(|_| Error::internal_error())?;
        let mut core = None;
        let mut db = None;
        let mut message = warnings
            .iter()
            .any(|w| w.starts_with("Cannot use") || w.starts_with("Workspace root"))
            .then(|| warnings.join("; "));
        if !matches!(options.adapter.as_str(), "lsp" | "vscode" | "vs") {
            return Err(Error::invalid_params("adapter must be lsp, vscode or vs"));
        }
        if !roots.is_empty() {
            match ue_core::WorkspaceSession::open(ue_core::SessionConfig::new(
                aliases
                    .iter()
                    .map(|(_, requested)| requested.to_string_lossy().into_owned())
                    .collect(),
                options.adapter,
            ))
            .await
            {
                Ok(session) => {
                    // Compatibility read connection for existing bounded LSP queries.
                    // All indexing, pruning and lifecycle remain exclusively core-owned.
                    let path = session.status().cache_path;
                    db = spawn_blocking(move || Db::open(path))
                        .await
                        .ok()
                        .and_then(|v| v.ok());
                    if db.is_none() {
                        warnings.push("LSP workspace read connection unavailable; standard features use open buffers only.".into());
                    }
                    core = Some(Arc::new(session));
                }
                Err(error) => {
                    message = Some(format!(
                        "Workspace unavailable; serving open documents in memory only: {error}"
                    ));
                    warnings.push(message.clone().unwrap());
                }
            }
        }
        let (ai, ai_error) = options.ai.client();
        if let Some(error) = &ai_error {
            warnings.push(error.clone());
        }
        {
            let mut state = self.state();
            if let Some(session) = &core {
                state.session_id = session.status().session_id;
            }
            state.roots = roots;
            state.root_aliases = aliases;
            state.db = db;
            state.core = core;
            state.message = message;
            state.style = options.style;
            state.ai = ai;
            state.ai_error = ai_error;
        }
        for warning in warnings {
            self.warning(Some(warning)).await;
        }
        Ok(InitializeResult {
            capabilities: ServerCapabilities {
                position_encoding: Some(PositionEncodingKind::UTF16),
                text_document_sync: Some(TextDocumentSyncCapability::Options(
                    TextDocumentSyncOptions {
                        open_close: Some(true),
                        change: Some(TextDocumentSyncKind::FULL),
                        save: Some(TextDocumentSyncSaveOptions::SaveOptions(SaveOptions {
                            include_text: Some(false),
                        })),
                        ..Default::default()
                    },
                )),
                completion_provider: Some(CompletionOptions {
                    trigger_characters: Some(vec![
                        "(".into(),
                        ",".into(),
                        ".".into(),
                        ">".into(),
                        ":".into(),
                    ]),
                    ..Default::default()
                }),
                hover_provider: Some(HoverProviderCapability::Simple(true)),
                definition_provider: Some(OneOf::Left(true)),
                document_symbol_provider: Some(OneOf::Left(true)),
                workspace_symbol_provider: Some(OneOf::Left(true)),
                experimental: Some(serde_json::json!({"penguin":{"protocolVersion":1}})),
                ..Default::default()
            },
            server_info: Some(ServerInfo {
                name: "penguin-lsp".into(),
                version: Some(env!("CARGO_PKG_VERSION").into()),
            }),
        })
    }

    async fn initialized(&self, _: InitializedParams) {
        let mut state = self.state();
        if state.initialized || state.stopping {
            return;
        }
        state.initialized = true;
        if let Some(core) = &state.core {
            let _ = core.reindex();
            state.indexer = Some(());
        }
    }

    async fn shutdown(&self) -> Result<()> {
        let _gate = self.diagnostic_gate.lock().await;
        let (core, ai_jobs, uris) = {
            let mut state = self.state();
            state.stopping = true;
            state.indexer = None;
            state.diagnostic_epoch += 1;
            for (_, task) in std::mem::take(&mut state.diagnostics) {
                task.abort();
            }
            state.ai_jobs.cancel_all();
            state.db = None;
            (
                state.core.clone(),
                std::mem::take(&mut state.core_ai_jobs),
                state.buffers.keys().cloned().collect::<Vec<_>>(),
            )
        };
        for uri in uris {
            self.client.publish_diagnostics(uri, Vec::new(), None).await;
        }
        if let Some(core) = core {
            for id in ai_jobs {
                let _ = core.cancel_job(&id);
            }
            // Drain accepted index/save/watch work before releasing the writer lease.
            core.close(false)
                .await
                .map_err(extension_handlers::core_error)?;
        }
        Ok(())
    }

    async fn did_change_configuration(&self, params: DidChangeConfigurationParams) {
        self.configure_style(params.settings).await;
    }

    async fn did_open(&self, params: DidOpenTextDocumentParams) {
        let item = params.text_document;
        let revision = Arc::new(Buffer {
            version: item.version,
            source: item.text,
            parsed: OnceLock::new(),
        });
        let diagnostic_guard = self.diagnostic_gate.lock().await;
        let accepted = {
            let mut state = self.state();
            if state.stopping
                || state
                    .buffers
                    .get(&item.uri)
                    .is_some_and(|old| old.version >= item.version)
            {
                false
            } else {
                state.buffers.insert(item.uri.clone(), revision.clone());
                true
            }
        };
        drop(diagnostic_guard);
        if accepted {
            self.schedule_diagnostics(item.uri, revision.clone());
            self.parse_revision(revision).await;
        } else {
            self.warning(Some("Ignored stale or stopped didOpen.".into()))
                .await;
        }
    }

    async fn did_change(&self, params: DidChangeTextDocumentParams) {
        if params
            .content_changes
            .iter()
            .any(|change| change.range.is_some() || change.range_length.is_some())
        {
            self.warning(Some(
                "Ignored ranged didChange: this server negotiated FULL synchronization.".into(),
            ))
            .await;
            return;
        }
        let Some(change) = params.content_changes.into_iter().last() else {
            return;
        };
        let revision = Arc::new(Buffer {
            version: params.text_document.version,
            source: change.text,
            parsed: OnceLock::new(),
        });
        let diagnostic_guard = self.diagnostic_gate.lock().await;
        let accepted = {
            let mut state = self.state();
            if !state.stopping
                && state
                    .buffers
                    .get(&params.text_document.uri)
                    .is_some_and(|old| revision.version > old.version)
            {
                state
                    .buffers
                    .insert(params.text_document.uri.clone(), revision.clone());
                true
            } else {
                false
            }
        };
        drop(diagnostic_guard);
        if accepted {
            self.schedule_diagnostics(params.text_document.uri, revision.clone());
            self.parse_revision(revision).await;
        } else {
            self.warning(Some(
                "Ignored stale didChange or change for a closed document.".into(),
            ))
            .await;
        }
    }

    async fn did_close(&self, params: DidCloseTextDocumentParams) {
        let _gate = self.diagnostic_gate.lock().await;
        {
            let mut state = self.state();
            state.buffers.remove(&params.text_document.uri);
            if let Some(task) = state.diagnostics.remove(&params.text_document.uri) {
                task.abort();
            }
        }
        self.client
            .publish_diagnostics(params.text_document.uri, Vec::new(), None)
            .await;
    }

    async fn did_save(&self, params: DidSaveTextDocumentParams) {
        // Ignore optional text: save indexes disk, never unsaved editor content.
        self.queue_files([params.text_document.uri]).await;
    }

    async fn did_change_watched_files(&self, params: DidChangeWatchedFilesParams) {
        self.queue_files(
            params
                .changes
                .into_iter()
                .filter(|event| {
                    matches!(
                        event.typ,
                        FileChangeType::CREATED | FileChangeType::CHANGED | FileChangeType::DELETED
                    )
                })
                .map(|event| event.uri),
        )
        .await;
    }

    async fn completion(&self, params: CompletionParams) -> Result<Option<CompletionResponse>> {
        self.run_request(move |snapshot| {
            let params = params.text_document_position;
            let Some(buffer) = snapshot.buffers.get(&params.text_document.uri) else {
                return (None, None);
            };
            let document = buffer.document();
            let (_, candidates, warning) =
                snapshot.candidates(Query::Prefix(document.prefix_at(params.position)));
            let items = features::completion::complete(
                document,
                params.position,
                &candidates,
                RESULT_LIMIT,
            );
            (
                Some(CompletionResponse::List(CompletionList {
                    is_incomplete: true,
                    items,
                })),
                warning,
            )
        })
        .await
    }

    async fn hover(&self, params: HoverParams) -> Result<Option<Hover>> {
        self.run_request(move |snapshot| {
            let params = params.text_document_position_params;
            let Some(buffer) = snapshot.buffers.get(&params.text_document.uri) else {
                return (None, None);
            };
            let document = buffer.document();
            let Some(word) = document.word_at(params.position) else {
                return (None, None);
            };
            let (_, candidates, warning) = snapshot.candidates(Query::Exact(word));
            (
                features::hover::hover(document, params.position, &candidates),
                warning,
            )
        })
        .await
    }

    async fn goto_definition(
        &self,
        params: GotoDefinitionParams,
    ) -> Result<Option<GotoDefinitionResponse>> {
        self.run_request(move |snapshot| {
            let params = params.text_document_position_params;
            let uri = params.text_document.uri;
            let Some(buffer) = snapshot.buffers.get(&uri) else {
                return (None, None);
            };
            let document = buffer.document();
            let Some(word) = document.word_at(params.position) else {
                return (None, None);
            };
            let (indexed, _, warning) = snapshot.candidates(Query::Exact(word));
            let locations = features::nav::definitions(&uri, document, params.position, &indexed);
            (Some(GotoDefinitionResponse::Array(locations)), warning)
        })
        .await
    }

    async fn document_symbol(
        &self,
        params: DocumentSymbolParams,
    ) -> Result<Option<DocumentSymbolResponse>> {
        self.run_request(move |snapshot| {
            let result = snapshot
                .buffers
                .get(&params.text_document.uri)
                .map(|buffer| {
                    DocumentSymbolResponse::Nested(features::nav::document_symbols(
                        buffer.document(),
                    ))
                });
            (result, None)
        })
        .await
    }

    async fn symbol(
        &self,
        params: WorkspaceSymbolParams,
    ) -> Result<Option<Vec<SymbolInformation>>> {
        self.run_request(move |snapshot| {
            let (indexed, _, warning) = snapshot.candidates(Query::Search(&params.query));
            (
                Some(features::nav::workspace_symbols(
                    &params.query,
                    &indexed,
                    RESULT_LIMIT,
                )),
                warning,
            )
        })
        .await
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use tower_lsp::LspService;

    const OLD: &str = "UCLASS()\nclass AOld {};\n";
    const NEW: &str = "UCLASS()\nclass ANew {};\n";

    fn service() -> LspService<Backend> {
        let (service, socket) = LspService::new(Backend::new);
        // Direct backend tests do not have a client reading log notifications.
        drop(socket);
        service
    }

    fn uri() -> Url {
        Url::parse("untitled:Scratch.h").unwrap()
    }

    async fn open(backend: &Backend, uri: Url, version: i32, text: &str) {
        backend
            .did_open(DidOpenTextDocumentParams {
                text_document: TextDocumentItem::new(uri, "cpp".into(), version, text.into()),
            })
            .await;
    }

    async fn change(backend: &Backend, uri: Url, version: i32, text: &str, range: Option<Range>) {
        backend
            .did_change(DidChangeTextDocumentParams {
                text_document: VersionedTextDocumentIdentifier::new(uri, version),
                content_changes: vec![TextDocumentContentChangeEvent {
                    range,
                    range_length: None,
                    text: text.into(),
                }],
            })
            .await;
    }

    async fn close(backend: &Backend, uri: Url) {
        backend
            .did_close(DidCloseTextDocumentParams {
                text_document: TextDocumentIdentifier::new(uri),
            })
            .await;
    }

    fn position(uri: Url, line: u32, character: u32) -> TextDocumentPositionParams {
        TextDocumentPositionParams::new(
            TextDocumentIdentifier::new(uri),
            Position::new(line, character),
        )
    }

    async fn completions(
        backend: &Backend,
        uri: Url,
        line: u32,
        character: u32,
    ) -> Vec<CompletionItem> {
        match backend
            .completion(CompletionParams {
                text_document_position: position(uri, line, character),
                work_done_progress_params: Default::default(),
                partial_result_params: Default::default(),
                context: None,
            })
            .await
            .unwrap()
        {
            Some(CompletionResponse::List(list)) => list.items,
            Some(CompletionResponse::Array(items)) => items,
            None => Vec::new(),
        }
    }

    async fn workspace(backend: &Backend, query: &str) -> Vec<SymbolInformation> {
        backend
            .symbol(WorkspaceSymbolParams {
                query: query.into(),
                work_done_progress_params: Default::default(),
                partial_result_params: Default::default(),
            })
            .await
            .unwrap()
            .unwrap()
    }

    #[tokio::test]
    async fn no_root_initialization_advertises_real_supported_capabilities() {
        let service = service();
        let backend = service.inner();
        let result = backend
            .initialize(InitializeParams::default())
            .await
            .unwrap();
        let caps = result.capabilities;
        match caps.text_document_sync.unwrap() {
            TextDocumentSyncCapability::Options(options) => {
                assert_eq!(options.change, Some(TextDocumentSyncKind::FULL));
                assert_eq!(options.open_close, Some(true));
            }
            _ => panic!("expected full sync options"),
        }
        assert!(caps.completion_provider.is_some());
        assert!(caps.hover_provider.is_some());
        assert!(caps.definition_provider.is_some());
        assert!(caps.document_symbol_provider.is_some());
        assert!(caps.workspace_symbol_provider.is_some());
        assert!(backend.state().db.is_none());
        assert!(backend.state().roots.is_empty());
        backend.initialized(InitializedParams {}).await;
        assert!(backend.state().indexer.is_none());
        open(backend, uri(), 1, NEW).await;
        assert!(completions(backend, uri(), 2, 0)
            .await
            .iter()
            .any(|item| item.label == "ANew"));
        backend.shutdown().await.unwrap();
    }

    #[tokio::test]
    async fn revisions_reject_stale_and_ranged_changes_and_survive_close_reopen() {
        let service = service();
        let backend = service.inner();
        open(backend, uri(), 1, OLD).await;
        let old_revision = backend.snapshot().buffers[&uri()].clone();
        change(backend, uri(), 3, NEW, None).await;
        change(backend, uri(), 2, OLD, None).await;
        change(backend, uri(), 3, OLD, None).await;
        open(backend, uri(), 2, OLD).await;
        change(
            backend,
            uri(),
            4,
            "ranged is not a full file",
            Some(Range::default()),
        )
        .await;
        assert_eq!(backend.state().buffers[&uri()].version, 3);
        assert_eq!(backend.state().buffers[&uri()].source, NEW);
        close(backend, uri()).await;
        change(backend, uri(), 5, OLD, None).await;
        assert!(backend.state().buffers.is_empty());
        // Reopen resets the version domain. Finishing any old parse must not
        // republish into the map even if the new version is numerically smaller.
        open(backend, uri(), 1, NEW).await;
        backend.parse_revision(old_revision).await;
        assert_eq!(backend.state().buffers[&uri()].source, NEW);
        close(backend, uri()).await;
        assert!(completions(backend, uri(), 0, 0).await.is_empty());
    }

    #[tokio::test]
    async fn requests_parse_the_latest_snapshot_even_while_an_older_parse_is_pending() {
        let service = service();
        let backend = service.inner();
        let old = Arc::new(Buffer {
            version: 1,
            source: OLD.into(),
            parsed: OnceLock::new(),
        });
        backend.state().buffers.insert(uri(), old.clone());
        let latest = Arc::new(Buffer {
            version: 2,
            source: NEW.into(),
            parsed: OnceLock::new(),
        });
        backend.state().buffers.insert(uri(), latest);
        backend.parse_revision(old).await;
        let items = completions(backend, uri(), 2, 0).await;
        assert!(items.iter().any(|item| item.label == "ANew"));
        assert!(!items.iter().any(|item| item.label == "AOld"));
        let hover = backend
            .hover(HoverParams {
                text_document_position_params: position(uri(), 1, 8),
                work_done_progress_params: Default::default(),
            })
            .await
            .unwrap()
            .unwrap();
        assert!(serde_json::to_string(&hover).unwrap().contains("ANew"));
        let outline = backend
            .document_symbol(DocumentSymbolParams {
                text_document: TextDocumentIdentifier::new(uri()),
                work_done_progress_params: Default::default(),
                partial_result_params: Default::default(),
            })
            .await
            .unwrap()
            .unwrap();
        match outline {
            DocumentSymbolResponse::Nested(symbols) => assert_eq!(symbols[0].name, "ANew"),
            _ => panic!("expected document symbols"),
        }
        let definitions = backend
            .goto_definition(GotoDefinitionParams {
                text_document_position_params: position(uri(), 1, 8),
                work_done_progress_params: Default::default(),
                partial_result_params: Default::default(),
            })
            .await
            .unwrap()
            .unwrap();
        match definitions {
            GotoDefinitionResponse::Array(locations) => {
                assert_eq!(locations.len(), 1);
                assert_eq!(locations[0].uri, uri());
            }
            _ => panic!("expected definition locations"),
        }
    }

    async fn fixture() -> (tempfile::TempDir, Db, PathBuf) {
        spawn_blocking(|| {
            let temp = tempfile::tempdir().unwrap();
            let root = std::fs::canonicalize(temp.path()).unwrap();
            let source = root.join("Source #1.h");
            std::fs::write(&source, OLD).unwrap();
            let db = Db::open(root.join("test.db")).unwrap();
            let report = index::index_file(&db, &source);
            assert!(report.errors.is_empty(), "{:?}", report.errors);
            assert_eq!(report.indexed, 1);
            (temp, db, source)
        })
        .await
        .unwrap()
    }

    #[tokio::test]
    async fn all_open_files_override_disk_even_when_the_live_file_has_no_symbols() {
        let (temp, db, path) = fixture().await;
        let service = service();
        let backend = service.inner();
        backend.state().db = Some(db.clone());
        // Temp paths may use Windows 8.3 spelling while the DB uses canonical
        // paths. Overlay suppression must work even without workspace aliases.
        let file_uri = Url::from_file_path(temp.path().join(path.file_name().unwrap())).unwrap();
        open(backend, uri(), 1, "AOld").await;
        assert!(completions(backend, uri(), 0, 4)
            .await
            .iter()
            .any(|item| item.label == "AOld"));
        assert_eq!(workspace(backend, "AOld").await.len(), 1);
        // Empty buffers suppress the entire indexed file, not just same names.
        open(backend, file_uri.clone(), 1, "").await;
        assert!(completions(backend, uri(), 0, 4).await.is_empty());
        assert!(workspace(backend, "AOld").await.is_empty());
        change(backend, file_uri.clone(), 2, NEW, None).await;
        change(backend, uri(), 2, "ANew", None).await;
        assert!(completions(backend, uri(), 0, 4)
            .await
            .iter()
            .any(|item| item.label == "ANew"));
        assert_eq!(workspace(backend, "ANew").await.len(), 1);
        assert!(workspace(backend, "AOld").await.is_empty());
        let targets = backend
            .goto_definition(GotoDefinitionParams {
                text_document_position_params: position(uri(), 0, 4),
                work_done_progress_params: Default::default(),
                partial_result_params: Default::default(),
            })
            .await
            .unwrap()
            .unwrap();
        match targets {
            GotoDefinitionResponse::Array(locations) => {
                assert_eq!(locations.len(), 1);
                assert_eq!(locations[0].uri, file_uri);
            }
            _ => panic!("expected locations"),
        }
        close(backend, file_uri).await;
        assert!(workspace(backend, "ANew").await.is_empty());
        assert_eq!(workspace(backend, "AOld").await.len(), 1);
        // Neither open nor change persisted anything from the editor.
        spawn_blocking(move || {
            assert_eq!(db.find("AOld").unwrap().len(), 1);
            assert!(db.find("ANew").unwrap().is_empty());
        })
        .await
        .unwrap();
        drop(service);
        spawn_blocking(move || drop(temp)).await.unwrap();
    }

    #[tokio::test]
    async fn equal_names_in_different_files_remain_definition_candidates() {
        let (temp, db, first) = fixture().await;
        let copy_db = db.clone();
        let second = first.with_file_name("Another.h");
        let second_copy = second.clone();
        spawn_blocking(move || {
            std::fs::write(&second_copy, OLD).unwrap();
            assert!(index::index_file(&copy_db, &second_copy).errors.is_empty());
        })
        .await
        .unwrap();
        let service = service();
        let backend = service.inner();
        backend.state().db = Some(db);
        open(backend, uri(), 1, "AOld").await;
        let response = backend
            .goto_definition(GotoDefinitionParams {
                text_document_position_params: position(uri(), 0, 4),
                work_done_progress_params: Default::default(),
                partial_result_params: Default::default(),
            })
            .await
            .unwrap()
            .unwrap();
        match response {
            GotoDefinitionResponse::Array(locations) => {
                assert_eq!(locations.len(), 2);
                assert_ne!(locations[0].uri, locations[1].uri);
            }
            _ => panic!("expected array"),
        }
        drop(service);
        spawn_blocking(move || drop(temp)).await.unwrap();
    }

    #[tokio::test]
    async fn initialization_prefers_folders_and_database_failure_is_nonfatal() {
        let temp = spawn_blocking(|| tempfile::tempdir().unwrap())
            .await
            .unwrap();
        let folder = temp.path().join("workspace");
        let fallback = temp.path().join("fallback");
        let setup_folder = folder.clone();
        let setup_fallback = fallback.clone();
        spawn_blocking(move || {
            std::fs::create_dir_all(&setup_folder).unwrap();
            std::fs::create_dir_all(&setup_fallback).unwrap();
            // A file where the cache directory should be forces immediate failure.
            std::fs::write(setup_folder.join(".vs"), "not a directory").unwrap();
        })
        .await
        .unwrap();
        let service = service();
        let backend = service.inner();
        #[allow(deprecated)]
        let params = InitializeParams {
            root_uri: Some(Url::from_directory_path(&fallback).unwrap()),
            workspace_folders: Some(vec![WorkspaceFolder {
                uri: Url::from_directory_path(&folder).unwrap(),
                name: "workspace".into(),
            }]),
            ..Default::default()
        };
        backend.initialize(params).await.unwrap();
        assert_eq!(backend.state().roots.len(), 1);
        assert!(backend.state().db.is_none());
        assert!(identity(&backend.state().roots[0]).ends_with("workspace"));
        open(backend, uri(), 1, NEW).await;
        assert!(completions(backend, uri(), 2, 0)
            .await
            .iter()
            .any(|item| item.label == "ANew"));
        backend.shutdown().await.unwrap();
        drop(service);
        spawn_blocking(move || drop(temp)).await.unwrap();
    }

    #[tokio::test]
    async fn scan_save_and_watch_jobs_drain_on_shutdown_and_never_index_external_paths() {
        let temp = spawn_blocking(|| tempfile::tempdir().unwrap())
            .await
            .unwrap();
        let root = temp.path().join("Project");
        let source = root.join("Source.h");
        let deleted = root.join("Deleted.h");
        let outside = temp.path().join("ProjectExtra").join("Outside.h");
        let paths = (
            root.clone(),
            source.clone(),
            deleted.clone(),
            outside.clone(),
        );
        spawn_blocking(move || {
            std::fs::create_dir_all(&paths.0).unwrap();
            std::fs::create_dir_all(paths.3.parent().unwrap()).unwrap();
            std::fs::write(paths.1, OLD).unwrap();
            std::fs::write(paths.2, "UCLASS()\nclass ADeleted {};\n").unwrap();
            std::fs::write(paths.3, "UCLASS()\nclass AOutside {};\n").unwrap();
        })
        .await
        .unwrap();
        let service = service();
        let backend = service.inner();
        #[allow(deprecated)]
        let params = InitializeParams {
            root_uri: Some(Url::from_directory_path(&root).unwrap()),
            ..Default::default()
        };
        backend.initialize(params).await.unwrap();
        let db = backend.state().db.clone().unwrap();
        let cache_path = backend.state().core.as_ref().unwrap().status().cache_path;
        let db_copy = db.clone();
        let deleted_copy = deleted.clone();
        let source_copy = source.clone();
        spawn_blocking(move || {
            assert_eq!(
                db_copy.stats().unwrap().symbols,
                0,
                "initialize must not scan"
            );
            // Seed a row that will need removal, and save newer disk contents.
            assert_eq!(index::index_file(&db_copy, &deleted_copy).indexed, 1);
            std::fs::remove_file(deleted_copy).unwrap();
            std::fs::write(source_copy, NEW).unwrap();
        })
        .await
        .unwrap();
        backend.initialized(InitializedParams {}).await;
        backend
            .did_save(DidSaveTextDocumentParams {
                text_document: TextDocumentIdentifier::new(Url::from_file_path(&source).unwrap()),
                text: Some(OLD.into()), // must never overwrite saved disk contents
            })
            .await;
        backend
            .did_change_watched_files(DidChangeWatchedFilesParams {
                changes: vec![
                    FileEvent {
                        uri: Url::from_file_path(&deleted).unwrap(),
                        typ: FileChangeType::DELETED,
                    },
                    FileEvent {
                        uri: Url::from_file_path(&outside).unwrap(),
                        typ: FileChangeType::CREATED,
                    },
                ],
            })
            .await;
        backend.shutdown().await.unwrap();
        assert!(backend.state().indexer.is_none());
        assert!(backend.state().stopping);
        spawn_blocking(move || {
            assert_eq!(db.find("ANew").unwrap().len(), 1);
            assert!(db.find("AOld").unwrap().is_empty());
            assert!(db.find("ADeleted").unwrap().is_empty());
            assert!(db.find("AOutside").unwrap().is_empty());
            assert!(Path::new(&cache_path).is_file());
            assert!(cache_path.contains("core-v1"));
            assert!(!root.join(".vs/PenguinExtension/penguin_core.db").exists());
            assert!(!root.join(".vs/PenguinExtension/penguin_cache.db").exists());
        })
        .await
        .unwrap();
        drop(service);
        spawn_blocking(move || drop(temp)).await.unwrap();
    }

    #[test]
    fn root_containment_is_component_aware_and_rejects_parent_traversal() {
        let root = if cfg!(windows) {
            PathBuf::from("C:/Project")
        } else {
            PathBuf::from("/project")
        };
        assert!(within_roots(
            &root.join("Source/File.h"),
            std::slice::from_ref(&root)
        ));
        assert!(!within_roots(
            &root.join("../Outside.h"),
            std::slice::from_ref(&root)
        ));
        assert!(!within_roots(
            &root.with_file_name("ProjectExtra").join("File.h"),
            &[root]
        ));
        assert!(!within_roots(Path::new("relative/File.h"), &[]));
    }
}
