# Penguin protocol v1

This is the implementation contract for thin clients. Standard editor features use LSP 3.17 stdio Content-Length framing, FULL document updates, and UTF-16 positions. Custom methods below use JSON-RPC on that same connection. MCP is a separate process/transport, not this framing.

## Initialization

Clients pass explicit project roots as LSP workspaceFolders (rootUri is the fallback). Optional initializationOptions.penguin contains `adapter` (vscode, vs, or lsp), `engineRoots` (absolute directory paths, explicitly configured), `ai` (enabled boolean, endpoint, model), and `style` configuration. Engine roots supplement workspace roots. AI is disabled unless enabled. Loopback only; default endpoint http://127.0.0.1:11434 and model qwen2.5-coder:3b. No service launch or model download.

Server advertises capabilities.experimental.penguin.protocolVersion = 1. Unknown future versions require explicit client compatibility handling.

### Style configuration

`initializationOptions.penguin.style` uses camelCase keys. Omitted keys keep these defaults:

```json
{
  "enabled": true,
  "blueprintAccessConflicts": true,
  "editVisibilityConflicts": true,
  "structPrefix": false,
  "enumPrefix": false,
  "boolPropertyPrefix": false
}
```

The server also accepts snake_case aliases for the five rule keys, matching the independent `ue-style` library's serde configuration. Use camelCase in editor settings; unknown future keys are ignored. `enabled: false` disables all style diagnostics. Configure clients to restart after a settings change. These checks are conservative hints/warnings, not UnrealHeaderTool or compiler validation.

## DTOs

All keys are camelCase. Optional metadata may be null or absent. Do not invent it. `file` is an absolute native path (or an untitled URI for live documents). `line` is one-based macro line. Standard definition requests provide LSP URIs/ranges.

`Symbol`: `{id, name, kind, macroName, file, line, typeName?, specifiers: [{key, value?}], bases: string[], signature?, documentation?, owner?, qualifiedName?}`. `id` is an opaque session/revision-scoped reference; refresh search after a stale-reference error. Kind is class, struct, interface, enum, function, property, or delegate.

`Status`: `{protocolVersion: 1, sessionId, state, roots: string[], files: number, symbols: number, message?}`. State is ready, indexing, busy, unavailable, or stopping. Ready means the service is available, not that an initial scan has necessarily completed.

`Job`: `{id, sessionId, kind, state, result?, error?}`. State is queued, running, succeeded, failed, or cancelled. AI result is `{text, model}`; index result contains indexed/unchanged/removed counts and errors. Results are bounded and expire; clients stop polling terminal jobs. Workspace changes invalidate old jobs. AI output is preview-only and must not be executed or automatically applied.

## Methods

| Method | Parameters | Result |
| --- | --- | --- |
| penguin/status | {} | Status |
| penguin/symbols | {query: string, limit?: number} | Symbol[] |
| penguin/symbol | {id: string} | Symbol or null (stale/not found) |
| penguin/inheritance | {name: string} | {bases: string[], derived: Symbol[]} |
| penguin/reindex | {} | Job |
| penguin/job | {id: string} | Job or null (expired/not found) |
| penguin/cancelJob | {id: string} | {cancelled: boolean} |
| penguin/ai | {task: "explain" or "generate", source: string, instruction?: string} | Job |

Custom methods return promptly; indexing and inference run as background jobs. Clients poll at a modest interval (e.g. 500ms), honor cancellation, and use bounded timeouts. No arbitrary filesystem reads or writes are exposed. Style diagnostics use standard textDocument/publishDiagnostics; clear on close/disable. Client configuration changes restart the server so roots/options cannot diverge from its current session.

## Safety and lifecycle

Source is data, not instructions. Render output as plain text or escaped Markdown; never interpret model/source strings as HTML. Request IDs and document versions must not be reused across a restarted session. After shutdown, send exit and wait briefly before killing a stuck child. Process stderr is diagnostic-only; stdout contains protocol frames only. Clients report a failed Core backend without activating the legacy backend silently.
