# Penguin MCP

Headless Rust library plus `penguin-mcp`, an MCP stdio server built with the
**official `rmcp = 3.5.1`** SDK (exact pin, Rust >= 1.88). Version/API were checked
with `cargo search rmcp` / `cargo info rmcp`. The SDK owns initialization, protocol
negotiation, JSON-RPC dispatch, ping and cancellation notifications. This is
**newline-delimited JSON, not LSP Content-Length framing**.

## Build and launch

```sh
cargo build --release -p ue-mcp --bin penguin-mcp
cargo test -p ue-mcp
cargo clippy -p ue-mcp --all-targets -- -D warnings
```

Windows executable: `target/release/penguin-mcp.exe`. Example launch:

```text
penguin-mcp.exe --root D:/Games/MyGame --root D:/Shared/MyPlugin --engine-root D:/UE_5.5/Engine/Source
```

`--root` is repeatable and required. Roots must be absolute existing directories;
optional `--engine-root` adds one root. At most 16 input roots, canonicalized and
deduplicated by core. Roots are immutable at runtime. Core excludes generated/build
paths and symlink/junction redirects. Indexing is **explicit**: call `reindex` and
poll `job_status`. Cached symbols can be queried before scanning. No watcher or
access to another editor's unsaved buffers is implied.

The dedicated `.vs/PenguinExtension/core-v1/mcp-<root-hash>/` cache uses core's WAL
DB and OS writer lease. Another process for the same roots reports busy rather
than competing. The legacy cache is untouched.

## Registration example (not installed automatically)

For clients using `mcpServers`, manually adapt these absolute paths. Penguin does
not register itself in user settings. Use the executable, not `cargo run`.

```json
{
  "mcpServers": {
    "penguin": {
      "command": "D:/Tools/Penguin/penguin-mcp.exe",
      "args": [
        "--root", "D:/Games/MyGame",
        "--root", "D:/Shared/MyPlugin",
        "--engine-root", "D:/UE_5.5/Engine/Source"
      ]
    }
  }
}
```

No shell is required. Diagnostics, help, version and argument errors use stderr;
stdout contains SDK JSON-RPC only.

## Tools and actual v1 contract

Argument objects reject unknown fields; limits are validated, not silently
clamped. No tool accepts a filesystem path, replacement roots, command, endpoint
or model. Source/comments/model output are untrusted data, not host instructions.

| Tool | Arguments | Result |
| --- | --- | --- |
| `symbols_search` | `{query, limit?: 1..200}` | Bounded symbols with opaque IDs |
| `symbol_details` | `{id}` from search | Symbol plus optional core metadata |
| `inheritance` | `{id, limit?: 1..200}` | Core base-name chain; no guessed derived symbols |
| `index_status` | `{}` | Core session status plus `aiEnabled` |
| `reindex` | `{}` | Core job; cache mutation only |
| `job_status` | `{id}` job ID | Core index/style job status |
| `job_cancel` | `{id}` job ID | Cooperative cancellation and current status |
| `style_check` | `{id, naming?: false, limit?: 1..200}` | Job checking selected symbol's whole disk file |
| `ai_start` | `{task: "explain" or "generate", source, instruction?: ""}` | Explicit local-inference job |
| `ai_status` | `{id}` AI job ID | AI job status and preview |
| `ai_cancel` | `{id}` AI job ID | Cancel local HTTP work |

Search query is at most 512 UTF-8 bytes; empty query browses. Default query/style
limit is 50. IDs are process/revision scoped, with 2,048 remembered symbol IDs.
Search again after stale/evicted IDs. Search returns
`{apiVersion:1,sessionId,revision,symbols,truncated}`. Symbols expose core's name,
kind, macroName, specifiers, typeName, bases, line, byteRange plus `file` and `id`.
Details return `{apiVersion,sessionId,revision,symbol,metadata}`. Metadata is optional
and includes only what core actually establishes, never fabricated signatures or
ownership.

Jobs follow the implemented core DTO (not early conceptual examples):
`{apiVersion:1,sessionId,jobId,kind,state,revision,createdAtMs,startedAtMs?,
finishedAtMs?,result?,error?}`. States: queued, running, succeeded, failed,
cancelled. Stop polling terminal states; 500ms polling is sufficient. Core retains
32 finished jobs and allows 16 pending jobs. Style results contain
`{apiVersion,sessionId,revision,file,diagnostics,truncated,snapshot:"disk"}`.
Diagnostic `byteRange` uses UTF-8 byte offsets, not LSP UTF-16. Contradictory
Blueprint access/edit-visibility checks are default; naming checks are opt-in.

Output provides both `structuredContent` and matching JSON text. Bad schemas or
routing return JSON-RPC `-32602`. Operational failures use `isError:true` and
`{apiVersion:1,error:{code,message,...}}`. Inference failures become job errors.

## Explicit local AI only

AI is **off by default**, including all network requests. `--model` and `--endpoint`
require `--ai`. Example opt-in arguments:

```text
--ai --model qwen2.5-coder:3b --endpoint http://127.0.0.1:11434
```

Default model: `qwen2.5-coder:3b`; this does not claim it is installed. The `ue-ai`
client validates loopback-only HTTP, pins localhost and disables redirects, proxy
routing and retries. Missing service/model is reported, not silently substituted.
Penguin never starts Ollama, downloads a model, executes generated code or applies
source edits. Caller supplies all inference source context explicitly.

At most 4 active AI jobs (one HTTP request at a time), 16 retained finished jobs,
64 KiB source, 8 KiB instruction, 128 KiB serialized HTTP prompt, 256 KiB response,
1,024 output tokens and 60-second inference timeout by default. Completed result
is `{text,model,previewOnly:true}`. Index revision changes discard late previews.
Cancellation drops local HTTP I/O; a service may briefly continue its accepted
computation. No model child process exists for Penguin to terminate.

## Bounds and lifecycle

- 8 active tool operations. Cancelling a protocol request does not free its disk
  worker slot prematurely. If cancellation abandons a submission before its job
  handle is returned, a request-owned guard cancels that job, including a worker
  registering it after the request was dropped. Shutdown drains these operations.
- 512 KiB input line cap; overflow closes transport before unbounded buffering.
- 2 MiB serialized tool result cap, including duplicate text/structured content.
- Style cap: 1 MiB regular files, pre/post containment/redirect checks, bounded
  reads, cooperative cancellation and shared worker-local parser reuse.
- Core owns all indexing/scanning. Style read/parse/check runs on the serial core
  blocking worker, not Tokio's executor. AI uses an independent bounded queue.
- 30-second initialize timeout. SDK request cancellation notifications are
  supported. After a job handle has been returned, cancel using its job tool.
- EOF, Ctrl+C, failed initialization and oversized input cancel jobs and release
  the cache lease. MCP does not use LSP shutdown/exit requests.

Embedders call `serve(config, async_reader, async_writer, cancel_token)` or
`PenguinServer::open(config)` for an rmcp `ServerHandler`. Await `close()` when
hosting the handler directly for deterministic HTTP/worker/lease cleanup. The
binary owns this lifecycle automatically.

## Verification scope

Tests use real subprocesses and temporary Unreal headers, plus local HTTP mocks
only. They cover version negotiation, schemas, invalid arguments, escaped text,
root isolation, stale IDs, direct shared-core query parity, style/index results,
unchanged sources, disabled AI,
unavailable service, model preview, queue/cancel paths, oversized input, EOF and
lease reacquisition. No external model call, service launch, download or user
configuration registration is performed. Unix symlink tests are conditional;
Windows test execution does not establish Unix runtime compatibility. Live-model
and external MCP-client acceptance remain separate manual checks.
