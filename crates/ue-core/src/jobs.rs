//! Bounded serial jobs. Cancellation is cooperative; callers must check the token
//! around bounded work units. A cancelled job never publishes a late result.
use crate::dto::*;
use serde_json::Value;
use std::collections::{BTreeMap, VecDeque};
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc, Condvar, Mutex, MutexGuard,
};
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Debug, Clone, Default)]
pub struct CancellationToken(pub(crate) Arc<AtomicBool>);
impl CancellationToken {
    pub fn is_cancelled(&self) -> bool {
        self.0.load(Ordering::Acquire)
    }
    pub fn check(&self) -> Result<()> {
        if self.is_cancelled() {
            Err(CoreError::new(ErrorCode::Cancelled, "job cancelled"))
        } else {
            Ok(())
        }
    }
    pub(crate) fn cancel(&self) {
        self.0.store(true, Ordering::Release);
    }
}

/// Snapshot identity for adapter-supplied, explicitly requested work. Contains no
/// mutable database handle. Generic jobs must not write sources or editor buffers.
#[derive(Debug, Clone)]
pub struct JobContext {
    pub session_id: String,
    pub job_id: String,
    pub revision: u64,
    pub roots: Vec<String>,
    pub cancellation: CancellationToken,
}
pub(crate) type Task = Box<dyn FnOnce(JobContext) -> Result<Value> + Send + 'static>;
pub(crate) enum Work {
    Index,
    Files(Vec<std::path::PathBuf>),
    Custom(Task),
}
pub(crate) struct Pending {
    pub id: String,
    pub work: Work,
}
pub(crate) struct Record {
    pub status: JobStatus,
    pub token: CancellationToken,
}
pub(crate) struct State {
    pub queue: VecDeque<Pending>,
    pub jobs: BTreeMap<String, Record>,
    pub finished: VecDeque<String>,
    pub closing: bool,
    pub closed: bool,
    pub revision: u64,
    pub indexed_files: usize,
    pub indexed_symbols: usize,
}
pub(crate) struct Shared {
    pub state: Mutex<State>,
    pub wake: Condvar,
    pub capacity: usize,
    pub retention: usize,
}
impl Shared {
    pub fn new(capacity: usize, retention: usize, files: usize, symbols: usize) -> Self {
        Self {
            state: Mutex::new(State {
                queue: VecDeque::new(),
                jobs: BTreeMap::new(),
                finished: VecDeque::new(),
                closing: false,
                closed: false,
                revision: 0,
                indexed_files: files,
                indexed_symbols: symbols,
            }),
            wake: Condvar::new(),
            capacity,
            retention,
        }
    }
    pub fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }
    pub fn begin_close(&self, cancel: bool) {
        let mut state = self.lock();
        state.closing = true;
        if cancel {
            for job in state.jobs.values() {
                if !job.status.state.is_terminal() {
                    job.token.cancel();
                }
            }
        }
        self.wake.notify_all();
    }
    pub fn enqueue(&self, session_id: &str, kind: String, work: Work) -> Result<JobStatus> {
        let mut state = self.lock();
        if state.closing {
            return Err(CoreError::new(ErrorCode::Closed, "session is closing"));
        }
        if state.queue.len() >= self.capacity {
            return Err(CoreError::new(ErrorCode::QueueFull, "job queue is full"));
        }
        let id = uuid::Uuid::new_v4().to_string();
        let status = JobStatus {
            api_version: API_VERSION,
            session_id: session_id.into(),
            job_id: id.clone(),
            kind,
            state: JobState::Queued,
            revision: state.revision,
            created_at_ms: now(),
            started_at_ms: None,
            finished_at_ms: None,
            result: None,
            error: None,
        };
        state.jobs.insert(
            id.clone(),
            Record {
                status: status.clone(),
                token: CancellationToken::default(),
            },
        );
        state.queue.push_back(Pending { id, work });
        self.wake.notify_one();
        Ok(status)
    }
    pub fn finish(&self, id: &str, outcome: Result<Value>, index_job: bool) {
        let mut state = self.lock();
        if let Some(record) = state.jobs.get_mut(id) {
            record.status.finished_at_ms = Some(now());
            if record.token.is_cancelled()
                || matches!(&outcome, Err(e) if e.code == ErrorCode::Cancelled)
            {
                record.status.state = JobState::Cancelled;
                record.status.error = Some(CoreError::new(ErrorCode::Cancelled, "job cancelled"));
            } else {
                match outcome.and_then(|v| {
                    ensure_result_size(&v)?;
                    Ok(v)
                }) {
                    Ok(value) => {
                        let failed = index_job
                            && value
                                .get("report")
                                .and_then(|v| v.get("errors"))
                                .and_then(Value::as_array)
                                .is_some_and(|errors| !errors.is_empty());
                        record.status.state = if failed {
                            JobState::Failed
                        } else {
                            JobState::Succeeded
                        };
                        if failed {
                            record.status.error = Some(CoreError::new(
                                ErrorCode::IndexFailed,
                                "indexing completed with errors; see result.report",
                            ));
                        }
                        record.status.result = Some(value);
                    }
                    Err(error) => {
                        record.status.state = JobState::Failed;
                        record.status.error = Some(error);
                    }
                }
            }
        }
        state.finished.push_back(id.into());
        while state.finished.len() > self.retention {
            if let Some(old) = state.finished.pop_front() {
                state.jobs.remove(&old);
            }
        }
        self.wake.notify_all();
    }
}
pub(crate) fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis()
        .min(u64::MAX as u128) as u64
}

/// Counts serialized bytes without allocating a second large response buffer.
pub(crate) fn ensure_result_size(value: &impl serde::Serialize) -> Result<()> {
    struct Budget(usize);
    impl std::io::Write for Budget {
        fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
            if bytes.len() > self.0 {
                return Err(std::io::Error::other("response exceeds 1 MiB"));
            }
            self.0 -= bytes.len();
            Ok(bytes.len())
        }
        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }
    serde_json::to_writer(Budget(MAX_JOB_RESULT_BYTES), value)
        .map_err(|e| CoreError::new(ErrorCode::ResultTooLarge, e.to_string()))
}
