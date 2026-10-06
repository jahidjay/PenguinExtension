# Penguin Unreal Tools for VS Code

A thin desktop VS Code-compatible client for the Penguin native language server. Requires **VS Code API 1.85 or newer**, a trusted filesystem workspace, and a **protocol-v1** penguin-lsp binary on the extension host. It never falls back to the legacy Visual Studio backend.

## Build and test locally

Run in apps/vscode-ext (Node.js 22.12+ recommended for development/package tooling):

```sh
npm ci
npm run typecheck
npm test
npm run build
```

Tests compile strict TypeScript and use Node built-in tests for trust/configuration, protocol DTOs and mocked requests, bounded job polling, restart ordering, watcher coalescing/disposal, and wire identifiers. Tests need no VS Code, model service or native binary. The runtime dependency is vscode-languageclient; no duplicate completion/hover/navigation providers are registered.

## Native binary and packaging

Supply the independently built protocol-v1 executable before packaging:

```text
bin/win32-x64/penguin-lsp.exe
bin/win32-arm64/penguin-lsp.exe
bin/linux-x64/penguin-lsp
bin/linux-arm64/penguin-lsp
bin/darwin-x64/penguin-lsp
bin/darwin-arm64/penguin-lsp
```

Only intended platforms need be present. Unix binaries need execute permission. Selection uses the extension host platform/architecture, not the UI machine. Remote SSH/WSL require the remote binary and remote absolute paths. No web build, download, installation script or model bootstrap is provided.

```sh
npm run package:check
npm run package
```

Preflight rejects missing, empty or non-executable current-platform binaries. Packaging uses the local @vscode/vsce CLI and writes dist/penguin-vscode.vsix. All staged bin contents are included; source, tests, maps, scripts and development dependencies are excluded. For another platform, set PENGUIN_PACKAGE_TARGET to its folder name before running these scripts. That changes preflight only; the package is universal and runtime selection remains host-specific. Preflight checks availability, not binary architecture/signature or protocol behavior.

No normal IDE installation is needed for development. After building and supplying a binary, manually start an Extension Development Host or launch a separate development host with:

```sh
code --extensionDevelopmentPath=<absolute-path-to-apps/vscode-ext> <project-folder>
```

Trust that project when you intend to run the backend. **Live VS Code and Antigravity runtimes have not been verified.** Other editors must implement the same desktop VS Code extension APIs.

## Settings and trust

Use user or workspace-level settings. One session covers all workspace folders; folder-specific settings are not merged. Workspace plus engine roots are limited to 16. Folder additions/removals and Penguin settings changes immediately invalidate commands and serialize shutdown/startup from a fresh snapshot.

| Setting | Default | Purpose |
| --- | --- | --- |
| penguin.server.path | empty | Optional absolute trusted executable path, without arguments or shell interpolation. Empty uses packaged binary. |
| penguin.engineRoots | [] | Explicit absolute engine directories on the extension host. No discovery. |
| penguin.style | {} | Style object passed unchanged; use enabled, blueprintAccessConflicts, editVisibilityConflicts, structPrefix, enumPrefix, and boolPropertyPrefix. See the protocol defaults. |
| penguin.ai.enabled | false | Explicit opt-in to local AI. |
| penguin.ai.endpoint | http://127.0.0.1:11434 | HTTP loopback origin only: localhost, IPv4 127/8 or IPv6 ::1; no credentials, path, query or fragment. |
| penguin.ai.model | qwen2.5-coder:3b | Configured local model; must already be installed before enabling AI. No download or fallback model selection. |

Untrusted workspaces are unsupported. Runtime guards prevent settings reads, executable overrides, spawning and source-bearing commands before trust. VS Code controls trust; revocation normally reloads the host. No service launch or model download. Use a local service you control; the backend must independently enforce loopback and reject external redirects.

Missing/incompatible binaries show an actionable error and Penguin Output entry. Correct the package/override, then Restart Backend. Startup is bounded at 20 seconds, custom requests at 10 seconds, and shutdown allows two seconds for LSP shutdown/exit followed by a brief child-exit wait before terminating a stuck process. No silent fallback or automatic crash loop.

## Editor features and commands

C++ file and untitled documents use standard LSP completion, hover, definition, outline/document symbols, workspace symbols and diagnostics. File headers classified as C (h/hpp/hh/hxx) are included. C source files are not Unreal documents. vscode-languageclient owns didOpen/didChange/didClose and FULL sync; no manual text update pipeline is added. UTF-16 positions remain unchanged. A wire adapter assigns monotonic request IDs/document versions across backend restarts and maps diagnostics versions back to editor versions.

| Command (Penguin category) | Behavior |
| --- | --- |
| Show Backend Status | Fetch status; show plain JSON in Output. |
| Reindex Workspace | Start background reindex with cancellable progress; report counts/errors in Output. |
| Search Unreal Symbols | Search up to 100 results, then navigate, inspect details or inheritance. |
| Show Symbol Details | Search and show supplied metadata in a separate untitled plaintext document. Missing metadata is not invented. |
| Browse Inheritance | Browse base names/derived symbols and navigate. Resolve bases explicitly rather than guessing. |
| Explain Selection (Local AI) | Send selected text only and show an unsaved separate plaintext preview. |
| Generate Preview (Local AI) | Prompt for an instruction; selection is optional context. Show an unsaved separate plaintext preview. |
| Cancel Active Jobs | Request cancellation using current session/job IDs. |
| Restart Backend | Ordered stop/start with latest trusted roots/options. |
| Show Output | Diagnostic channel, also usable without trust. |

Status refreshes every five seconds. Ready means service available, not necessarily initial scan complete. Jobs poll at 500ms with a three-minute deadline and at most four active command jobs. Cancellation is cooperative: a pending initial submission must return its job ID before cancelJob can address it. Session and job IDs are checked after asynchronous operations; stale results are not shown and old jobs never cancel work in a replacement session. Refresh search for stale/expired symbol references.

AI output remains plaintext, unsaved and separate from source. No apply button, workspace edits, automatic save, execution or HTML/webviews. Review generated content manually. Selected source is capped at 64 KiB, instructions at 4096 UTF-8 bytes, results at 1 MiB. Hover/completion documentation is escaped to plain text; completion command execution is stripped.

## Watchers and fixed contract

Workspace/engine roots get session-owned watchers. Eligible extensions: h/hpp/hh/hxx/c/cpp/cc/cxx/inl/uproject/uplugin. Exclusions: .git/.vs/.idea/.vscode/node_modules, Binaries, Intermediate, Saved, DerivedDataCache, target, generated.h and gen.cpp. Events debounce for 250ms with one-second maximum wait, coalesce by URI and send workspace/didChangeWatchedFiles in ordered batches of 128. Watchers never read source or send text updates and are disposed on restart/deactivation. Backend indexing must independently enforce root validation/exclusions. Very large engine trees remain subject to editor watcher limits.

Initialize uses explicit workspaceFolders, first-folder rootUri fallback, and initializationOptions.penguin containing adapter=vscode, engineRoots, style and ai. Require capabilities.experimental.penguin.protocolVersion=1, FULL sync and UTF-16. Unknown versions fail explicitly. The fixed contract is the repository docs/penguin-protocol.md; this adapter does not modify it.

Custom methods: penguin/status, penguin/symbols, penguin/symbol, penguin/inheritance, penguin/reindex, penguin/job, penguin/cancelJob, penguin/ai. Symbols use opaque string id, absolute native file (or untitled URI) and one-based line; jobs use id and sessionId. These are NOT the internal Core DTOs (apiVersion/reference/jobId/envelopes); translation belongs in the LSP server. No contract changes are required. Style keys and defaults are documented in the fixed contract; the settings object is passed through unchanged.
