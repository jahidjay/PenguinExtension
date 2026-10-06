# ue-core API (v1)

Headless disk-backed service; no editor overlays, implicit indexing, watcher, AI calls, or source writes. The LSP compatibility modules reexport only `index` and `parse`; server integration is separate.

## Rust surface

All types below are exported at `ue_core::*`. JSON fields and enum values use camelCase. `API_VERSION = 1`.

```rust
WorkspaceSession::open(config: SessionConfig) -> impl Future<Output = Result<WorkspaceSession>>
session.status() -> SessionStatus
session.reindex() -> Result<JobStatus>
session.files_changed(FileChangesRequest) -> impl Future<Output = Result<JobStatus>>
session.search(SearchRequest) -> impl Future<Output = Result<SymbolsResponse>>
session.file_symbols(FileSymbolsRequest) -> impl Future<Output = Result<SymbolsResponse>>
session.details(DetailRequest) -> impl Future<Output = Result<DetailResponse>>
session.inheritance(InheritanceRequest) -> impl Future<Output = Result<InheritanceResponse>>
session.submit_job(kind, FnOnce(JobContext) -> Result<serde_json::Value> + Send + 'static) -> Result<JobStatus>
session.job_status(id: &str) -> Result<JobStatus>
session.cancel_job(id: &str) -> Result<JobStatus>
session.close(cancel_pending: bool) -> impl Future<Output = Result<()>>
```

`SessionConfig::new(Vec<String>, adapter)` sets version/default capacities. Paths are absolute filesystem strings, not URIs. Queries/files/details are dispatched with `spawn_blocking`; indexing and generic jobs use one dedicated blocking worker. Status/job submission/cancellation only access memory. Generic jobs must use bounded inputs, check `context.cancellation.check()` between bounded operations, and must not write source; core does not expose its DB. AI/style composition remains the adapter's responsibility.

## JSON request schema

- SessionConfig: `{apiVersion:1, roots:string[], adapter:string, queueCapacity?:number=16, retainedJobs?:number=32}`
- SearchRequest: `{apiVersion:1, query:string, limit?:number=50}` (literal substring, SQLite ASCII case-insensitive)
- FileSymbolsRequest: `{apiVersion:1, file:string, limit?:number=50}`
- DetailRequest: `{apiVersion:1, reference:SymbolReference}`
- InheritanceRequest: `{apiVersion:1, reference:SymbolReference, limit?:number=50}`
- FileChangesRequest: `{apiVersion:1, files:string[]}` (creation/change/deletion inferred from disk)
- SymbolReference: `{sessionId:string, revision:number, file:string, symbolKey:string}`

Requests deny unknown fields. Unsupported versions/invalid bounds are errors, not silently clamped. `symbolKey` is an opaque SHA-256 declaration fingerprint; session identity and index revision are required. Reindex/file jobs advance the revision even if unchanged or partially cancelled, conservatively invalidating old references. A reopened session always gets a new UUID. Distinct files and overload ranges never use name-only IDs.

## JSON response schema

- SymbolsResponse: `{apiVersion:1, sessionId, revision, symbols:SymbolDto[], truncated:boolean}`
- DetailResponse: `{apiVersion:1, sessionId, revision, symbol:SymbolDto, metadata:MetadataDto|null}`
- InheritanceResponse: `{apiVersion:1, sessionId, revision, bases:string[], truncated:boolean}`
- SymbolDto: `{reference, name, kind, macroName, specifiers:[{key,value:string|null}], typeName:string|null, bases:string[], line:number, byteRange:{start,end}}`
- MetadataDto: `{owner:string|null, qualifiedName:string|null, signature:string|null, declaration:string|null, documentation:string|null, nameRange:{start,end}|null, declarationRange:{start,end}|null}` (syntax-proven only; optional rich extraction uses the metadata parser/database APIs)
- SessionStatus: `{apiVersion:1, sessionId, revision, roots, adapter, cacheNamespace, cachePath, closed, indexedFiles, indexedSymbols, queuedJobs, runningJobs, retainedJobs}`
- JobStatus: `{apiVersion:1, sessionId, jobId, kind, state, revision, createdAtMs, startedAtMs:number|null, finishedAtMs:number|null, result:JSON|null, error:CoreError|null}`
- Index job result: `{apiVersion:1, sessionId, revision, report:{indexed,unchanged,removed,errors:string[],errorsTruncated:boolean}}`
- CoreError: `{apiVersion:1, code, message}`

Index jobs with read/parse/cache errors are `failed` with the bounded partial report retained in `result`.

Job states: `queued`, `running`, `succeeded`, `failed`, `cancelled`. Error codes: `invalidInput`, `busy`, `closed`, `outsideRoots`, `excludedPath`, `notFound`, `staleReference`, `queueFull`, `cancelled`, `resultTooLarge`, `io`, `database`, `indexFailed`, `internal`. All source/model strings are untrusted data for clients to render as plain text/escaped Markdown.

Lines are one-based macro anchors; ranges are half-open UTF-8 byte offsets, not UTF-16 positions. Inheritance starts from the exact referenced declaration; expansion stops at unresolved or ambiguous base names instead of inventing C++ name resolution.

## Isolation and limits

Cache: `<first sorted canonical root>/.vs/PenguinExtension/core-v1/<adapter>-<sha256 canonical root set>/penguin_cache.db`. Canonical root order, duplicate aliases and redundant nested roots do not change the namespace. Adapters are isolated; the legacy `.vs/PenguinExtension/penguin_cache.db` remains untouched. A persistent `writer.lock` file holds an OS advisory exclusive lease until the worker and DB drain; it is never deleted. A second same-namespace session returns `busy` (including another process). Crash/exit releases the OS lock.

At most 8 concurrent blocking queries/file-validation tasks are admitted; excess callers get `busy`, rather than building an unbounded executor backlog. File-symbol/details use the existing per-file DB API (hydrating that single header internally); output is capped even though the intermediate row vector is not SQL-paginated.

Bounds: 1–16 roots; adapter 1–32 lowercase ASCII alphanumeric/underscore/hyphen; queue and terminal retention 1–64 each; 1–256 changed paths; paths <=32,768 UTF-8 bytes; queries <=512 bytes; result limits 1–200; serialized response/job result <=1 MiB; index reports retain <=32 error strings <=4096 bytes each. Terminal jobs are evicted oldest-first; querying an evicted ID returns `notFound`. Generic closures are trusted integration code, so adapters must bound captured inputs before submission. No thread can forcibly terminate an uncooperative closure.

`close(false)` stops submissions and drains all accepted jobs; `close(true)` also requests cooperative cancellation. It waits for DB/lease release and is idempotent. Dropping the last handle starts cancellation without blocking a UI thread; explicit close is needed before immediately reopening the same namespace. Cancelled/incomplete scans never authorize broad pruning; successfully committed individual files are not rolled back. Cancellation is checked between files/directories, not midway through a parser/SQLite operation.

Excluded case-insensitive directories: `.git`, `.vs`, `target`, `node_modules`, `Binaries`, `Intermediate`, `Saved`, `DerivedDataCache`. Only `.h`/`.hpp`, never `.generated.h`. Symlinks/junctions/reparse-point ancestors are not traversed. Existing aliases canonicalize; missing paths use their closest existing ancestor. Unicode/BOM offsets, UTF-8/NUL checks and 16 MiB read caps are preserved. Queries represent the last indexed disk snapshot, not unsaved buffers; explicit reindex/file notifications refresh deletion/content changes. Filesystem rechecks are best effort, not an OS sandbox against hostile concurrent path replacement.
