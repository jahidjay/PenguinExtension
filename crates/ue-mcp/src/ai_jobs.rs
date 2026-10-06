//! Small async-only inference queue, separate from the serial disk worker.
use serde_json::json;
use std::{
    collections::{BTreeMap, VecDeque},
    sync::{Arc, Mutex},
    time::{SystemTime, UNIX_EPOCH},
};
use tokio::{sync::Semaphore, task::JoinHandle};
use tokio_util::sync::CancellationToken;
use ue_ai::{AiClient, AiRequest};
use ue_core::{CoreError, ErrorCode, JobState, JobStatus, WorkspaceSession, API_VERSION};

const MAX_ACTIVE: usize = 4;
const RETAINED: usize = 16;
#[derive(Clone)]
pub(crate) struct AiJobs {
    inner: Arc<Inner>,
}
struct Inner {
    client: AiClient,
    slots: Semaphore,
    state: Mutex<State>,
    shutdown: CancellationToken,
}
#[derive(Default)]
struct State {
    sequence: u64,
    records: BTreeMap<String, Record>,
    finished: VecDeque<String>,
    tasks: Vec<JoinHandle<()>>,
}
struct Record {
    status: JobStatus,
    cancel: CancellationToken,
}
fn error(code: ErrorCode, message: &str) -> CoreError {
    CoreError::new(code, message)
}
fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .min(u64::MAX as u128) as u64
}
impl AiJobs {
    pub(crate) fn new(client: AiClient) -> Self {
        Self {
            inner: Arc::new(Inner {
                client,
                slots: Semaphore::new(1),
                state: Mutex::new(State::default()),
                shutdown: CancellationToken::new(),
            }),
        }
    }
    pub(crate) fn start(
        &self,
        session: &WorkspaceSession,
        request: AiRequest,
    ) -> ue_core::Result<JobStatus> {
        let mut state = self.inner.state.lock().unwrap_or_else(|e| e.into_inner());
        if self.inner.shutdown.is_cancelled() {
            return Err(error(ErrorCode::Closed, "AI queue is closed"));
        }
        if state
            .records
            .values()
            .filter(|r| !r.status.state.is_terminal())
            .count()
            >= MAX_ACTIVE
        {
            return Err(error(
                ErrorCode::QueueFull,
                "AI queue is full (4 active jobs maximum)",
            ));
        }
        state.tasks.retain(|task| !task.is_finished());
        state.sequence += 1;
        let snapshot = session.status();
        if snapshot.closed {
            return Err(error(ErrorCode::Closed, "workspace is closed"));
        }
        let id = format!("ai-{}-{}", snapshot.session_id, state.sequence);
        let status = JobStatus {
            api_version: API_VERSION,
            session_id: snapshot.session_id,
            job_id: id.clone(),
            kind: "ai".into(),
            state: JobState::Queued,
            revision: snapshot.revision,
            created_at_ms: now(),
            started_at_ms: None,
            finished_at_ms: None,
            result: None,
            error: None,
        };
        let cancel = self.inner.shutdown.child_token();
        state.records.insert(
            id.clone(),
            Record {
                status: status.clone(),
                cancel: cancel.clone(),
            },
        );
        let this = self.clone();
        let session = session.clone();
        state.tasks.push(tokio::spawn(async move {
            let outcome = tokio::select! {
                biased;
                _ = cancel.cancelled() => Err(error(ErrorCode::Cancelled, "AI job cancelled")),
                result = async {
                    let _permit = this.inner.slots.acquire().await.map_err(|_| error(ErrorCode::Closed, "AI queue is closed"))?;
                    {
                        let mut state = this.inner.state.lock().unwrap_or_else(|e| e.into_inner());
                        let record = state.records.get_mut(&id).ok_or_else(|| error(ErrorCode::NotFound, "AI job expired"))?;
                        if cancel.is_cancelled() { return Err(error(ErrorCode::Cancelled, "AI job cancelled")); }
                        record.status.state = JobState::Running;
                        record.status.started_at_ms = Some(now());
                    }
                    this.inner.client.generate(request).await.map_err(|e| error(ErrorCode::Internal, &e.to_string()))
                } => result,
            };
            let current = session.status();
            let mut state = this.inner.state.lock().unwrap_or_else(|e| e.into_inner());
            if let Some(record) = state.records.get_mut(&id) {
                record.status.finished_at_ms = Some(now());
                if cancel.is_cancelled() || current.closed {
                    record.status.state = JobState::Cancelled;
                    record.status.error = Some(error(ErrorCode::Cancelled, "AI job cancelled"));
                } else if current.session_id != record.status.session_id || current.revision != record.status.revision {
                    record.status.state = JobState::Failed;
                    record.status.error = Some(error(ErrorCode::StaleReference, "workspace revision changed; request a new preview"));
                } else {
                    match outcome {
                        Ok(response) => { record.status.state = JobState::Succeeded; record.status.result = Some(json!({"text":response.preview,"model":response.model,"previewOnly":true})); }
                        Err(error) => { record.status.state = JobState::Failed; record.status.error = Some(error); }
                    }
                }
            }
            state.finished.push_back(id);
            while state.finished.len() > RETAINED {
                if let Some(old) = state.finished.pop_front() { state.records.remove(&old); }
            }
        }));
        Ok(status)
    }
    pub(crate) fn status(&self, id: &str) -> ue_core::Result<JobStatus> {
        self.inner
            .state
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .records
            .get(id)
            .map(|r| r.status.clone())
            .ok_or_else(|| error(ErrorCode::NotFound, "unknown or evicted AI job"))
    }
    pub(crate) fn cancel(&self, id: &str) -> ue_core::Result<JobStatus> {
        let mut state = self.inner.state.lock().unwrap_or_else(|e| e.into_inner());
        let record = state
            .records
            .get_mut(id)
            .ok_or_else(|| error(ErrorCode::NotFound, "unknown or evicted AI job"))?;
        if !record.status.state.is_terminal() {
            record.cancel.cancel();
        }
        Ok(record.status.clone())
    }
    pub(crate) async fn close(&self) {
        self.inner.shutdown.cancel();
        let tasks = {
            std::mem::take(
                &mut self
                    .inner
                    .state
                    .lock()
                    .unwrap_or_else(|e| e.into_inner())
                    .tasks,
            )
        };
        for task in tasks {
            let _ = task.await;
        }
    }
}
