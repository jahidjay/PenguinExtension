//! Version 1 transport contract. JSON uses camelCase, paths are absolute strings,
//! lines are 1-based and byte ranges are UTF-8 half-open (not LSP positions).
use serde::{Deserialize, Serialize};
use serde_json::Value;

pub const API_VERSION: u32 = 1;
pub const MAX_RESULTS: usize = 200;
pub const MAX_QUERY_BYTES: usize = 512;
pub const MAX_PATH_BYTES: usize = 32_768;
pub const MAX_ROOTS: usize = 16;
pub const MAX_CHANGED_FILES: usize = 256;
pub const MAX_JOB_RESULT_BYTES: usize = 1024 * 1024;
pub const MAX_ERROR_BYTES: usize = 4096;

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum ErrorCode {
    InvalidInput,
    Busy,
    Closed,
    OutsideRoots,
    ExcludedPath,
    NotFound,
    StaleReference,
    QueueFull,
    Cancelled,
    ResultTooLarge,
    Io,
    Database,
    IndexFailed,
    Internal,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct CoreError {
    pub api_version: u32,
    pub code: ErrorCode,
    pub message: String,
}
impl CoreError {
    pub fn new(code: ErrorCode, message: impl Into<String>) -> Self {
        let mut message = message.into();
        if message.len() > MAX_ERROR_BYTES {
            let mut end = MAX_ERROR_BYTES;
            while !message.is_char_boundary(end) {
                end -= 1;
            }
            message.truncate(end);
        }
        Self {
            api_version: API_VERSION,
            code,
            message,
        }
    }
}
impl std::fmt::Display for CoreError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{}", self.message)
    }
}
impl std::error::Error for CoreError {}
impl From<std::io::Error> for CoreError {
    fn from(e: std::io::Error) -> Self {
        Self::new(ErrorCode::Io, e.to_string())
    }
}
impl From<ue_db::DbError> for CoreError {
    fn from(e: ue_db::DbError) -> Self {
        Self::new(ErrorCode::Database, e.to_string())
    }
}
pub type Result<T> = std::result::Result<T, CoreError>;

fn default_queue() -> usize {
    16
}
fn default_retention() -> usize {
    32
}
fn default_limit() -> usize {
    50
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionConfig {
    pub api_version: u32,
    pub roots: Vec<String>,
    /// Stable adapter name, e.g. lsp, mcp or desktop. Never a filesystem path.
    pub adapter: String,
    #[serde(default = "default_queue")]
    pub queue_capacity: usize,
    #[serde(default = "default_retention")]
    pub retained_jobs: usize,
}
impl SessionConfig {
    pub fn new(roots: Vec<String>, adapter: impl Into<String>) -> Self {
        Self {
            api_version: API_VERSION,
            roots,
            adapter: adapter.into(),
            queue_capacity: default_queue(),
            retained_jobs: default_retention(),
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SearchRequest {
    pub api_version: u32,
    pub query: String,
    #[serde(default = "default_limit")]
    pub limit: usize,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FileSymbolsRequest {
    pub api_version: u32,
    pub file: String,
    #[serde(default = "default_limit")]
    pub limit: usize,
}
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SymbolReference {
    pub session_id: String,
    pub revision: u64,
    pub file: String,
    /// Opaque fingerprint of this declaration including its exact macro range.
    pub symbol_key: String,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct DetailRequest {
    pub api_version: u32,
    pub reference: SymbolReference,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct InheritanceRequest {
    pub api_version: u32,
    pub reference: SymbolReference,
    #[serde(default = "default_limit")]
    pub limit: usize,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct FileChangesRequest {
    pub api_version: u32,
    pub files: Vec<String>,
}
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct ByteRange {
    pub start: usize,
    pub end: usize,
}
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SpecifierDto {
    pub key: String,
    pub value: Option<String>,
}
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub struct SymbolDto {
    pub reference: SymbolReference,
    pub name: String,
    pub kind: String,
    pub macro_name: String,
    pub specifiers: Vec<SpecifierDto>,
    pub type_name: Option<String>,
    pub bases: Vec<String>,
    pub line: usize,
    pub byte_range: ByteRange,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SymbolsResponse {
    pub api_version: u32,
    pub session_id: String,
    pub revision: u64,
    pub symbols: Vec<SymbolDto>,
    pub truncated: bool,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MetadataDto {
    pub owner: Option<String>,
    pub qualified_name: Option<String>,
    pub signature: Option<String>,
    pub declaration: Option<String>,
    pub documentation: Option<String>,
    pub name_range: Option<ByteRange>,
    pub declaration_range: Option<ByteRange>,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct DetailResponse {
    pub api_version: u32,
    pub session_id: String,
    pub revision: u64,
    pub symbol: SymbolDto,
    pub metadata: Option<MetadataDto>,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct InheritanceResponse {
    pub api_version: u32,
    pub session_id: String,
    pub revision: u64,
    /// Nearest first. Names only: unresolved/ambiguous C++ bases are not guessed.
    pub bases: Vec<String>,
    pub truncated: bool,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct IndexReportDto {
    pub indexed: usize,
    pub unchanged: usize,
    pub removed: usize,
    pub errors: Vec<String>,
    pub errors_truncated: bool,
}
#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "camelCase")]
pub enum JobState {
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}
impl JobState {
    pub fn is_terminal(self) -> bool {
        matches!(self, Self::Succeeded | Self::Failed | Self::Cancelled)
    }
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct JobStatus {
    pub api_version: u32,
    pub session_id: String,
    pub job_id: String,
    pub kind: String,
    pub state: JobState,
    pub revision: u64,
    pub created_at_ms: u64,
    pub started_at_ms: Option<u64>,
    pub finished_at_ms: Option<u64>,
    pub result: Option<Value>,
    pub error: Option<CoreError>,
}
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionStatus {
    pub api_version: u32,
    pub session_id: String,
    pub revision: u64,
    pub roots: Vec<String>,
    pub adapter: String,
    pub cache_namespace: String,
    pub cache_path: String,
    pub closed: bool,
    pub indexed_files: usize,
    pub indexed_symbols: usize,
    pub queued_jobs: usize,
    pub running_jobs: usize,
    pub retained_jobs: usize,
}
