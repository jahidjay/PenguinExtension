mod files;
mod settings;
use serde_json::{json, Value};
use settings::Settings;
use std::{
    path::PathBuf,
    sync::{Arc, Mutex},
    time::Duration,
};
use tauri::{Manager, State};
use tauri_plugin_dialog::DialogExt;
use ue_core::{
    DetailRequest, InheritanceRequest, SearchRequest, SessionConfig, SymbolReference,
    WorkspaceSession, API_VERSION,
};
type Result<T> = std::result::Result<T, String>;
struct Active {
    session: WorkspaceSession,
    settings: Settings,
    ai_job: Mutex<Option<String>>,
    index_job: Mutex<Option<String>>,
}
pub struct DesktopState {
    active: Mutex<Option<Arc<Active>>>,
    transition: tokio::sync::Mutex<()>,
    config: PathBuf,
    reads: tokio::sync::Semaphore,
}
impl DesktopState {
    fn new(config: PathBuf) -> Self {
        Self {
            active: Mutex::new(None),
            transition: tokio::sync::Mutex::new(()),
            config,
            reads: tokio::sync::Semaphore::new(4),
        }
    }
    fn active(&self, id: &str) -> Result<Arc<Active>> {
        if id.is_empty() || id.len() > 128 {
            return Err("Invalid session identifier".into());
        }
        self.active
            .lock()
            .map_err(|_| "Workspace lock failed")?
            .as_ref()
            .filter(|a| a.session.status().session_id == id)
            .cloned()
            .ok_or_else(|| "Workspace session changed; discard this result and reopen".into())
    }
    fn check(&self, id: &str) -> Result<()> {
        self.active(id).map(|_| ())
    }
    async fn close_current(&self) -> Result<()> {
        let old = self
            .active
            .lock()
            .map_err(|_| "Workspace lock failed")?
            .take();
        if let Some(old) = old {
            old.session.close(true).await.map_err(|e| e.to_string())?;
        }
        Ok(())
    }
    async fn open_root(&self, root: String) -> Result<Value> {
        // Validate settings before closing the old session; cancelled dialogs never get here.
        let config = self.config.clone();
        let settings = blocking(move || settings::load(&config)).await?;
        self.close_current().await?;
        let session = WorkspaceSession::open(SessionConfig::new(vec![root], "desktop"))
            .await
            .map_err(|e| e.to_string())?;
        let status = status_value(&session);
        *self.active.lock().map_err(|_| "Workspace lock failed")? = Some(Arc::new(Active {
            session,
            settings,
            ai_job: Mutex::new(None),
            index_job: Mutex::new(None),
        }));
        Ok(status)
    }
}
async fn blocking<T: Send + 'static>(
    work: impl FnOnce() -> Result<T> + Send + 'static,
) -> Result<T> {
    tauri::async_runtime::spawn_blocking(work)
        .await
        .map_err(|e| e.to_string())?
}
fn status_value(session: &WorkspaceSession) -> Value {
    let s = session.status();
    json!({"protocolVersion":s.api_version,"sessionId":s.session_id,"state":if s.closed {"stopping"} else if s.running_jobs+s.queued_jobs>0 {"busy"} else {"ready"},"roots":s.roots,"files":s.indexed_files,"symbols":s.indexed_symbols})
}
fn symbol_value(symbol: ue_core::SymbolDto) -> Result<Value> {
    let id = serde_json::to_string(&symbol.reference).map_err(|e| e.to_string())?;
    let file = symbol.reference.file.clone();
    let mut value = serde_json::to_value(symbol).map_err(|e| e.to_string())?;
    let object = value.as_object_mut().ok_or("Invalid core symbol")?;
    object.remove("reference");
    object.insert("id".into(), json!(id));
    object.insert("file".into(), json!(file));
    Ok(value)
}
fn reference(id: &str, session: &str) -> Result<SymbolReference> {
    if id.len() > 32768 {
        return Err("Symbol reference exceeds limit".into());
    }
    let reference: SymbolReference =
        serde_json::from_str(id).map_err(|_| "Invalid symbol reference")?;
    if reference.session_id != session {
        return Err("Stale symbol session".into());
    }
    Ok(reference)
}
fn job_value(job: ue_core::JobStatus) -> Value {
    json!({"id":job.job_id,"sessionId":job.session_id,"kind":if job.kind=="indexWorkspace" {"index"} else {&job.kind},"state":job.state,"result":job.result,"error":job.error.map(|e|e.message)})
}
#[tauri::command]
async fn desktop_open(
    app: tauri::AppHandle,
    state: State<'_, DesktopState>,
) -> Result<Option<Value>> {
    let _guard = state
        .transition
        .try_lock()
        .map_err(|_| "Workspace transition already running")?;
    let (tx, rx) = tokio::sync::oneshot::channel();
    app.dialog()
        .file()
        .set_title("Choose an Unreal project or source directory")
        .pick_folder(move |folder| {
            let _ = tx.send(folder);
        });
    let Some(folder) = rx.await.map_err(|_| "Folder dialog closed unexpectedly")? else {
        return Ok(None);
    };
    let path = folder.into_path().map_err(|e| e.to_string())?;
    let root = path
        .to_str()
        .ok_or("Workspace path must be Unicode")?
        .to_owned();
    state.open_root(root).await.map(Some)
}
#[tauri::command]
async fn desktop_close(session_id: String, state: State<'_, DesktopState>) -> Result<()> {
    let _guard = state
        .transition
        .try_lock()
        .map_err(|_| "Workspace transition already running")?;
    state.check(&session_id)?;
    state.close_current().await
}
#[tauri::command]
async fn desktop_status(session_id: String, state: State<'_, DesktopState>) -> Result<Value> {
    Ok(status_value(&state.active(&session_id)?.session))
}
#[tauri::command]
async fn desktop_search(
    session_id: String,
    query: String,
    state: State<'_, DesktopState>,
) -> Result<Vec<Value>> {
    let _permit = state
        .reads
        .try_acquire()
        .map_err(|_| "Too many concurrent requests; retry shortly")?;
    let active = state.active(&session_id)?;
    let result = active
        .session
        .search(SearchRequest {
            api_version: API_VERSION,
            query,
            limit: 100,
        })
        .await
        .map_err(|e| e.to_string())?;
    state.check(&session_id)?;
    result.symbols.into_iter().map(symbol_value).collect()
}
#[tauri::command]
async fn desktop_symbol(
    session_id: String,
    id: String,
    state: State<'_, DesktopState>,
) -> Result<Value> {
    let _permit = state
        .reads
        .try_acquire()
        .map_err(|_| "Too many concurrent requests; retry shortly")?;
    let active = state.active(&session_id)?;
    let result = active
        .session
        .details(DetailRequest {
            api_version: API_VERSION,
            reference: reference(&id, &session_id)?,
        })
        .await
        .map_err(|e| e.to_string())?;
    state.check(&session_id)?;
    let mut value = symbol_value(result.symbol)?;
    if let Some(metadata) = result.metadata {
        let extra = serde_json::to_value(metadata).map_err(|e| e.to_string())?;
        if let Some(object) = extra.as_object() {
            value
                .as_object_mut()
                .ok_or("Invalid symbol")?
                .extend(object.clone());
        }
    }
    Ok(value)
}
#[tauri::command]
async fn desktop_inheritance(
    session_id: String,
    id: String,
    state: State<'_, DesktopState>,
) -> Result<Value> {
    let _permit = state
        .reads
        .try_acquire()
        .map_err(|_| "Too many concurrent requests; retry shortly")?;
    let active = state.active(&session_id)?;
    let result = active
        .session
        .inheritance(InheritanceRequest {
            api_version: API_VERSION,
            reference: reference(&id, &session_id)?,
            limit: 100,
        })
        .await
        .map_err(|e| e.to_string())?;
    state.check(&session_id)?;
    Ok(
        json!({"bases":result.bases,"derived":[],"derivedAvailable":false,"truncated":result.truncated}),
    )
}
#[tauri::command]
async fn desktop_reindex(session_id: String, state: State<'_, DesktopState>) -> Result<Value> {
    let active = state.active(&session_id)?;
    let mut slot = active.index_job.lock().map_err(|_| "Job lock failed")?;
    if let Some(id) = slot.as_ref() {
        if let Ok(job) = active.session.job_status(id) {
            if !job.state.is_terminal() {
                return Ok(job_value(job));
            }
        }
    }
    let job = active.session.reindex().map_err(|e| e.to_string())?;
    *slot = Some(job.job_id.clone());
    Ok(job_value(job))
}
#[tauri::command]
async fn desktop_job(
    session_id: String,
    id: String,
    state: State<'_, DesktopState>,
) -> Result<Option<Value>> {
    let active = state.active(&session_id)?;
    match active.session.job_status(&id) {
        Ok(job) => Ok(Some(job_value(job))),
        Err(e) if e.code == ue_core::ErrorCode::NotFound => Ok(None),
        Err(e) => Err(e.to_string()),
    }
}
#[tauri::command]
async fn desktop_cancel(
    session_id: String,
    id: String,
    state: State<'_, DesktopState>,
) -> Result<bool> {
    let active = state.active(&session_id)?;
    let job = active.session.cancel_job(&id).map_err(|e| e.to_string())?;
    Ok(!job.state.is_terminal() || job.state == ue_core::JobState::Cancelled)
}
#[tauri::command]
async fn desktop_style(
    session_id: String,
    file: String,
    state: State<'_, DesktopState>,
) -> Result<Value> {
    let _permit = state
        .reads
        .try_acquire()
        .map_err(|_| "Too many concurrent requests; retry shortly")?;
    let active = state.active(&session_id)?;
    // Reuse core path/exclusion validation before the bounded source-local style read.
    active
        .session
        .file_symbols(ue_core::FileSymbolsRequest {
            api_version: API_VERSION,
            file: file.clone(),
            limit: 1,
        })
        .await
        .map_err(|e| e.to_string())?;
    let roots = active.session.status().roots;
    let naming = active.settings.naming_checks;
    let result=blocking(move||{
  let (file,source)=files::read_header(&file,&roots)?;
  let parsed=ue_core::parse::parse_source(&source);
  let config=ue_style::StyleConfig{struct_prefix:naming,enum_prefix:naming,bool_property_prefix:naming,..Default::default()};
  let diagnostics:Vec<Value>=ue_style::check(&source,&parsed,&config).into_iter().take(200).map(|d|{
   let before=&source[..d.byte_range.start]; let line=before.bytes().filter(|b|*b==10).count()+1;
   let column=before.rsplit(char::from(10)).next().unwrap_or_default().chars().count()+1;
   json!({"code":d.code,"severity":d.severity,"message":d.message,"line":line,"column":column})
  }).collect(); Ok(json!({"file":file,"diagnostics":diagnostics}))
 }).await?;
    state.check(&session_id)?;
    Ok(result)
}
#[tauri::command]
async fn desktop_ai(
    session_id: String,
    task: ue_ai::AiTask,
    source: String,
    instruction: String,
    state: State<'_, DesktopState>,
) -> Result<Value> {
    let active = state.active(&session_id)?;
    if !active.settings.ai_enabled {
        return Err("Local AI is disabled. Enable it explicitly in Settings.".into());
    }
    if source.trim().is_empty() || source.len() > 32768 || instruction.len() > 2048 {
        return Err(
            "Provide context of 1..32768 UTF-8 bytes and instruction of at most 2048 bytes".into(),
        );
    }
    let mut slot = active.ai_job.lock().map_err(|_| "Job lock failed")?;
    if let Some(id) = slot.as_ref() {
        if let Ok(job) = active.session.job_status(id) {
            if !job.state.is_terminal() {
                return Err("One AI request is already queued or running".into());
            }
        }
    }
    let job = submit_ai(
        &active.session,
        active.settings.ai_config(),
        ue_ai::AiRequest {
            task,
            source,
            instruction,
        },
    )?;
    *slot = Some(job.job_id.clone());
    Ok(job_value(job))
}
fn submit_ai(
    session: &WorkspaceSession,
    config: ue_ai::AiConfig,
    request: ue_ai::AiRequest,
) -> Result<ue_core::JobStatus> {
    session.submit_job("ai", move |context| {
  let error=|e:String|ue_core::CoreError::new(ue_core::ErrorCode::Internal,e);
  context.cancellation.check()?;
  let runtime=tokio::runtime::Builder::new_current_thread().enable_all().build().map_err(|e|error(e.to_string()))?;
  runtime.block_on(async move {
   let client=ue_ai::AiClient::new(config).map_err(|e|error(e.to_string()))?;
   let cancelled=async {loop {context.cancellation.check()?;tokio::time::sleep(Duration::from_millis(40)).await;}};
   tokio::select! {
    result=client.generate(request)=>{let result=result.map_err(|e|error(e.to_string()))?;context.cancellation.check()?;Ok(json!({"text":result.preview,"model":result.model}))},
    result=cancelled=>result,
   }
  })
 }).map_err(|e|e.to_string())
}
#[tauri::command]
async fn desktop_settings(state: State<'_, DesktopState>) -> Result<Settings> {
    let path = state.config.clone();
    blocking(move || settings::load(&path)).await
}
#[tauri::command]
async fn desktop_save_settings(settings: Settings, state: State<'_, DesktopState>) -> Result<()> {
    let _guard = state
        .transition
        .try_lock()
        .map_err(|_| "Workspace transition already running")?;
    if state
        .active
        .lock()
        .map_err(|_| "Workspace lock failed")?
        .is_some()
    {
        return Err("Close the workspace before changing settings".into());
    }
    let path = state.config.clone();
    blocking(move || settings::save(&path, &settings)).await
}
pub fn run() {
    let app = tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .setup(|app| {
            let config = app.path().app_config_dir()?.join("settings.json");
            app.manage(DesktopState::new(config));
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            desktop_open,
            desktop_close,
            desktop_status,
            desktop_search,
            desktop_symbol,
            desktop_inheritance,
            desktop_reindex,
            desktop_job,
            desktop_cancel,
            desktop_style,
            desktop_ai,
            desktop_settings,
            desktop_save_settings
        ])
        .build(tauri::generate_context!())
        .expect("Could not start Penguin Desktop");
    let quitting = Arc::new(std::sync::atomic::AtomicBool::new(false));
    app.run(move |handle, event| {
        if let tauri::RunEvent::ExitRequested { api, .. } = event {
            if !quitting.swap(true, std::sync::atomic::Ordering::SeqCst) {
                api.prevent_exit();
                let handle = handle.clone();
                tauri::async_runtime::spawn(async move {
                    let state = handle.state::<DesktopState>();
                    let _guard = state.transition.lock().await;
                    let _ = state.close_current().await;
                    handle.exit(0);
                });
            }
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture() -> (tempfile::TempDir, String) {
        let d = tempfile::tempdir().unwrap();
        let root = d.path().join("Game");
        std::fs::create_dir(&root).unwrap();
        std::fs::write(root.join("Actor.h"),"UCLASS() class ATest : public AActor { GENERATED_BODY() UPROPERTY(BlueprintReadOnly, BlueprintReadWrite) bool badName; };").unwrap();
        (d, root.to_string_lossy().into_owned())
    }
    #[tokio::test]
    async fn open_index_search_detail_close_and_reopen() {
        let (d, root) = fixture();
        let state = DesktopState::new(d.path().join("settings.json"));
        let status = state.open_root(root.clone()).await.unwrap();
        let id = status["sessionId"].as_str().unwrap();
        let active = state.active(id).unwrap();
        let job = active.session.reindex().unwrap();
        loop {
            let job = active.session.job_status(&job.job_id).unwrap();
            if job.state.is_terminal() {
                assert_eq!(job.state, ue_core::JobState::Succeeded, "{:?}", job.error);
                break;
            }
            tokio::time::sleep(Duration::from_millis(10)).await;
        }
        let results = active
            .session
            .search(SearchRequest {
                api_version: 1,
                query: "ATest".into(),
                limit: 100,
            })
            .await
            .unwrap();
        assert_eq!(results.symbols.len(), 1);
        let symbol = symbol_value(results.symbols.into_iter().next().unwrap()).unwrap();
        assert_eq!(symbol["name"], "ATest");
        let opaque = symbol["id"].as_str().unwrap();
        let details = active
            .session
            .details(DetailRequest {
                api_version: 1,
                reference: reference(opaque, id).unwrap(),
            })
            .await
            .unwrap();
        assert_eq!(details.symbol.name, "ATest");
        let inheritance = active
            .session
            .inheritance(InheritanceRequest {
                api_version: 1,
                reference: reference(opaque, id).unwrap(),
                limit: 100,
            })
            .await
            .unwrap();
        assert!(inheritance.bases.contains(&"AActor".into()));
        assert!(reference(opaque, "other-session").is_err());
        state.close_current().await.unwrap();
        assert!(state.active(id).is_err());
        let new = state.open_root(root).await.unwrap();
        assert_ne!(new["sessionId"], status["sessionId"]);
        state.close_current().await.unwrap();
    }
    #[tokio::test]
    async fn close_cancels_pending_work_and_releases_writer_lease() {
        let (d, root) = fixture();
        let state = DesktopState::new(d.path().join("settings.json"));
        state.open_root(root.clone()).await.unwrap();
        let active = state.active.lock().unwrap().clone().unwrap();
        let job = active
            .session
            .submit_job("test", move |ctx| loop {
                ctx.cancellation.check()?;
                std::thread::sleep(Duration::from_millis(5));
            })
            .unwrap();
        tokio::time::timeout(Duration::from_secs(3), state.close_current())
            .await
            .unwrap()
            .unwrap();
        assert_eq!(
            active.session.job_status(&job.job_id).unwrap().state,
            ue_core::JobState::Cancelled
        );
        state.open_root(root).await.unwrap();
        state.close_current().await.unwrap();
    }
    #[tokio::test]
    async fn invalid_root_leaves_no_stale_session() {
        let (d, root) = fixture();
        let state = DesktopState::new(d.path().join("settings.json"));
        state.open_root(root).await.unwrap();
        assert!(state
            .open_root(d.path().join("missing").to_string_lossy().into_owned())
            .await
            .is_err());
        assert!(state.active.lock().unwrap().is_none());
    }
    #[test]
    fn reference_input_is_bounded_and_session_checked() {
        assert!(reference("not json", "a").is_err());
        assert!(reference(&"x".repeat(32769), "a").is_err());
    }
    #[test]
    fn frontend_and_acl_expose_only_specific_commands() {
        let capability: Value =
            serde_json::from_str(include_str!("../capabilities/desktop.json")).unwrap();
        let permissions = capability["permissions"].as_array().unwrap();
        assert_eq!(permissions.len(), 13);
        for permission in permissions {
            assert!(permission.as_str().unwrap().starts_with("allow-desktop-"));
        }
        let config: Value = serde_json::from_str(include_str!("../tauri.conf.json")).unwrap();
        let csp = config["app"]["security"]["csp"].as_str().unwrap();
        assert!(!csp.contains("unsafe-inline"));
        assert!(!csp.contains("https:"));
        assert!(csp.contains("default-src 'none'"));
    }
    #[test]
    fn style_library_receives_same_bounded_parsed_snapshot() {
        let (d, root) = fixture();
        let (file, source) = files::read_header(
            &PathBuf::from(&root).join("Actor.h").to_string_lossy(),
            &[root],
        )
        .unwrap();
        let parsed = ue_core::parse::parse_source(&source);
        let diagnostics = ue_style::check(&source, &parsed, &Default::default());
        assert!(file.ends_with("Actor.h"));
        assert_eq!(diagnostics.len(), 1);
        assert_eq!(diagnostics[0].code, "UE_STYLE_001");
        drop(d);
    }
    #[tokio::test]
    async fn ai_mock_success_is_a_plain_text_preview() {
        use std::io::{Read, Write};
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let endpoint = format!("http://{}", listener.local_addr().unwrap());
        let server = std::thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            stream
                .set_read_timeout(Some(Duration::from_secs(3)))
                .unwrap();
            let mut bytes = [0u8; 16384];
            let _ = stream.read(&mut bytes).unwrap();
            let body=serde_json::to_string(&json!({"model":"test-model","response":"<script>untrusted</script>","done":true,"eval_count":5})).unwrap();
            let response=format!("HTTP/1.1 200 OK{crlf}Content-Type: application/json{crlf}Content-Length: {len}{crlf}Connection: close{crlf}{crlf}{body}",crlf=String::from_utf8(vec![13,10]).unwrap(),len=body.len());
            stream.write_all(response.as_bytes()).unwrap();
        });
        let (d, root) = fixture();
        let state = DesktopState::new(d.path().join("settings.json"));
        let status = state.open_root(root).await.unwrap();
        let active = state.active(status["sessionId"].as_str().unwrap()).unwrap();
        let config = ue_ai::AiConfig {
            endpoint,
            model: "test-model".into(),
            timeout_ms: 3000,
            ..Default::default()
        };
        let job = submit_ai(
            &active.session,
            config,
            ue_ai::AiRequest {
                task: ue_ai::AiTask::Explain,
                source: "UCLASS() class ATest {};".into(),
                instruction: String::new(),
            },
        )
        .unwrap();
        let outcome = tokio::time::timeout(Duration::from_secs(5), async {
            loop {
                let next = active.session.job_status(&job.job_id).unwrap();
                if next.state.is_terminal() {
                    break next;
                }
                tokio::time::sleep(Duration::from_millis(10)).await;
            }
        })
        .await
        .unwrap();
        assert_eq!(
            outcome.state,
            ue_core::JobState::Succeeded,
            "{:?}",
            outcome.error
        );
        assert_eq!(
            job_value(outcome)["result"]["text"],
            "<script>untrusted</script>"
        );
        state.close_current().await.unwrap();
        server.join().unwrap();
    }
    #[tokio::test]
    async fn ai_cancellation_drops_http_and_discards_late_result() {
        use std::io::Read;
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let endpoint = format!("http://{}", listener.local_addr().unwrap());
        let (tx, rx) = tokio::sync::oneshot::channel();
        let server = std::thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            stream
                .set_read_timeout(Some(Duration::from_secs(3)))
                .unwrap();
            let mut bytes = [0u8; 16384];
            let _ = stream.read(&mut bytes);
            let _ = tx.send(());
            std::thread::sleep(Duration::from_millis(250));
        });
        let (d, root) = fixture();
        let state = DesktopState::new(d.path().join("settings.json"));
        let status = state.open_root(root).await.unwrap();
        let active = state.active(status["sessionId"].as_str().unwrap()).unwrap();
        let job = submit_ai(
            &active.session,
            ue_ai::AiConfig {
                endpoint,
                model: "test-model".into(),
                ..Default::default()
            },
            ue_ai::AiRequest {
                task: ue_ai::AiTask::Generate,
                source: "class ATest;".into(),
                instruction: String::new(),
            },
        )
        .unwrap();
        tokio::time::timeout(Duration::from_secs(3), rx)
            .await
            .unwrap()
            .unwrap();
        active.session.cancel_job(&job.job_id).unwrap();
        tokio::time::timeout(Duration::from_secs(2), state.close_current())
            .await
            .unwrap()
            .unwrap();
        let outcome = active.session.job_status(&job.job_id).unwrap();
        assert_eq!(outcome.state, ue_core::JobState::Cancelled);
        assert!(outcome.result.is_none());
        server.join().unwrap();
    }
}
