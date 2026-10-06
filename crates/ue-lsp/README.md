# penguin-lsp

Unreal reflection language server for the PenguinCore Rust workspace. This is
the stdio backend for thin Penguin clients. The reusable `server::service()`
builder registers standard LSP and every method in `docs/penguin-protocol.md`.

## Build and run

From the repository root:

```sh
cargo build -p ue-lsp
cargo run -p ue-lsp --bin penguin-lsp
cargo test -p ue-lsp
```

The binary uses LSP over stdin/stdout, not an interactive shell. Configure an LSP
client to launch `target/debug/penguin-lsp` (`penguin-lsp.exe` on Windows), with
C++ documents and the Unreal project directory as the workspace root. Protocol
messages are the only output on stdout.

## Implemented

- Reflected symbol and macro-name completion, with macro-specific specifier
  catalogs, prefix filtering and UTF-16 text edits.
- Hover for recorded symbol facts and known macro specifiers.
- Exact-name definition candidates, flat document outlines and workspace search.
- Full-document synchronization with version checks and live, unsaved buffers
  taking precedence over indexed files, including unsaved deletions.
- Background `.h`/`.hpp` workspace scans, content-hash change detection, and
  disk reindexing on save or client-provided watched-file notifications.
- Core-owned, namespaced WAL cache and writer lease under
  `.vs/PenguinExtension/core-v1/<adapter>-<root-hash>/penguin_cache.db`.
  One core FIFO handles scans, pruning, saves and watched-file changes. No legacy
  writer runs, and neither legacy cache location is touched.
- In-memory open-buffer features when there is no local workspace or the cache
  cannot be opened.

The indexer ignores generated headers, build/cache directories and symlink or
junction traversal. It accepts UTF-8 headers up to 16 MiB, retaining a UTF-8 BOM
for correct byte offsets. Read failures preserve existing symbols. Only a
successful traversal can prune confirmed missing files.

## Deliberate limits

- Only reflected declarations are indexed. This is not a general C++ compiler
  language server; it complements rather than replaces clangd/IntelliSense.
- Rich custom symbol details include owner, qualified name, signature and docs
  only when the parser proves them. This is not member/type resolution:
  completion after `.`, `->` or `::` has no global fallback. Same-name
  definition candidates may be ambiguous.
- Navigation anchors point to the reflection macro, not a guessed declaration
  name. Disk-only locations use its recorded line and column zero.
- The catalog does not yet complete metadata keys inside `meta=(...)`.
- Clients must send **FULL** document updates. Ranged changes are rejected.
- Watched-file events must be supplied by the client; there is no filesystem
  watcher or automatic watcher registration. Explicit `engineRoots` supplement
  workspace folders. Root/AI configuration changes require restarting the server;
  style changes additionally support clearing/rechecking via LSP configuration.
- Standard queries are capped (200 returned items, 1,024 indexed candidates).
  Custom searches/derived-class candidates are capped at 200 with no pagination;
  derived results may be incomplete in large workspaces. Custom disk queries
  report busy after 750 ms rather than holding sequential dispatch indefinitely.
  Disk-only ancestry uses core expansion; with live overlays it reports proven
  direct bases rather than claiming a stale transitive chain.
- No type inference, automated refactoring, source writes or MCP transport.
- Transport dispatch is sequential to preserve buffer-notification ordering;
  request-level parallelism and cancellation remain future work. Background
  indexing is separate, with serialized writes drained on shutdown.

## Penguin protocol v1

Initialization advertises `experimental.penguin.protocolVersion = 1`. Pass
`initializationOptions.penguin` with `adapter` (`lsp`, `vs`, `vscode`), absolute
`engineRoots`, `ai` and `style`. Standard positions remain UTF-16 and updates FULL.

Custom requests: `penguin/status`, `penguin/symbols`, `penguin/symbol`,
`penguin/inheritance`, `penguin/reindex`, `penguin/job`, `penguin/cancelJob`,
`penguin/ai`. Scans and inference return jobs immediately; poll terminal state.
Live symbol IDs bind to the exact buffer Arc/version/session, and disk references
expire on core revisions. IDs are bounded (1,024 retained); stale details are null.
Rich disk metadata is loaded by the details method, never fabricated by search.

AI is disabled by default. Explicit enablement uses the ue-ai validated loopback
HTTP client (default `http://127.0.0.1:11434`, model `qwen2.5-coder:3b`). Only the
explicit request source/instruction is sent. There is no implicit source upload,
service launch, model download or output execution. Responses map to `{text,model}`
and are preview-only. At most two accepted AI jobs are active/queued. Workspace
AI uses the bounded core serial job interface; no-root AI uses an adapter map
(32 retained results, five-minute expiry). Shutdown cancels inference while
accepted index/save/watch work drains. HTTP cancellation stops local I/O, not
necessarily computation already accepted by the local service.

Style diagnostics are debounced 100 ms, capped at 500, converted from source byte
ranges to UTF-16 and guarded by URI, exact revision Arc, session and config epoch.
Close/disable clears diagnostics. `style.enabled` defaults true; contradictory
specifier checks default on and naming checks default off. CamelCase switches:
`blueprintAccessConflicts`, `editVisibilityConflicts`, `structPrefix`, `enumPrefix`,
`boolPropertyPrefix`. Diagnostics are warnings/hints, never compiler errors.

## Tests

Inline tests cover the feature handlers, index safety, coordinate conversion
and server state. `tests/stdio.rs` launches the actual binary to exercise LSP
framing, editor requests, custom capabilities/methods, rich metadata, stale IDs,
namespace leases, engine roots, reindex jobs, unsaved cross-file overlays, fake
loopback HTTP inference responsiveness/cancellation and shutdown, and diagnostic
revision/close/disable races. The index regressions now live in ue-core.
