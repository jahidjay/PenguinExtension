# AI Context for PenguinExtension / PenguinCore

This file is intended for AI assistants (like Claude, ChatGPT, Cursor, etc.) to quickly understand the architecture, purpose, and coding guidelines of the **PenguinExtension** project.

When generating code or suggesting changes for this repository, please adhere to the context and rules defined here.

## Current status — 2026-10-06 (supersedes the legacy context below)

The repository now contains **implemented PenguinCore components**, not just a planned C# extension rewrite. The root VSSDK/WPF extension remains present and its **Visual Studio bridge has passed headless and package checks**. Do not treat the legacy-only overview, feature requests, or "Where to Start" instruction below as the current implementation backlog.

**Evidence authority:** [docs/verification.md](docs/verification.md) records measured results and their limits. Its latest component/integration entries supersede its earlier baseline; this summary does not turn builds or unit tests into interactive acceptance. Test/build claims below refer to the recorded executions in that ledger.

| Component | Current implementation and verification status |
| --- | --- |
| `ue-parser`, `ue-db`, `ue-core` | Implemented and tested: tree-sitter-backed reflection parsing, pooled SQLite/WAL persistence, disk-backed sessions, bounded queries/jobs, stale-reference checks, and isolated cache writer leases. |
| `ue-style`, `ue-ai` | Implemented and tested: conservative reflection/style checks and bounded, opt-in loopback Ollama preview requests. AI tests use local HTTP mocks, not a live model. |
| `ue-lsp`, `ue-mcp` | Implemented and tested stdio adapters. The recorded headless suite passed 268 tests, including 11 LSP and 11 MCP real-process tests; Windows release executables were produced. See the ledger for count conventions and exact commands. |
| `apps/vscode-ext` | Implemented; strict typecheck, 27 unit tests, production compilation, Windows-x64 package inspection and extracted-server smoke checks passed. Interactive VS Code/Antigravity acceptance remains unverified. |
| `apps/desktop-tauri` | Implemented; 24 frontend tests, 20 native tests and recorded build/lint checks passed after source-read hardening. Windows executable and NSIS installer rebuilt with the fix; installer not installed and GUI acceptance unverified. |
| Root Visual Studio VSIX / bridge | Implemented; 21 bridge/DTO assertions and builds using VS2022 and VS18 MSBuild passed. The 71-entry VSIX and extracted-server smoke check passed. Interactive IDE acceptance remains unverified; installation range stays `[17.0, 18.0)`. |

### Current architecture and boundaries

- Shared Rust crates live under `crates/`; VS Code and Tauri clients live under `apps/`. The desktop native crate has its own Cargo workspace and separate checks.
- `ue-core` owns disk indexing, bounded jobs and root/adapter-isolated WAL caches under `.vs/PenguinExtension/core-v1/<adapter>-<root-hash>/`; the legacy cache is not migrated or shared. LSP owns live unsaved-buffer overlays; desktop/MCP queries are disk-backed and require explicit refresh/reindex rather than implying shared IDE buffers.
- LSP clients use [Penguin protocol v1](docs/penguin-protocol.md), not internal core DTOs. MCP uses its separate SDK-backed newline-delimited JSON transport, not LSP framing.
- AI is disabled by default and returns preview text only. Enabling it does not launch a service, install `qwen2.5-coder:3b`, execute output, or apply source edits. Live-model acceptance is unverified.
- Reflected-symbol tooling is not full C++ type inference, member resolution, automated refactoring, or Rider parity. See the [LSP limits](crates/ue-lsp/README.md); member-scoped completion and `meta=(...)` key completion are not established features.
- Cross-platform support is a design target, not a verified release matrix. Windows headless/build results do not establish Linux/macOS/ARM runtime or package compatibility. No interactive VS2022/2026, VS Code/Antigravity, or desktop GUI pass is claimed.

### Read before changing a component

Use the [core API](crates/ue-core/README.md), [LSP](crates/ue-lsp/README.md), [MCP](crates/ue-mcp/README.md), [style](crates/ue-style/README.md), [AI](crates/ue-ai/README.md), [VS Code client](apps/vscode-ext/README.md), and [desktop](apps/desktop-tauri/README.md) documentation. The [manual acceptance checklist](docs/manual-acceptance.md) describes checks still requiring actual execution; a checklist is not a pass record.

Preserve the root C# extension's namespace, legacy project format, MEF content type, UI-thread safety, and WAL/thread-safety requirements. Its regex-only guidance describes the legacy indexer; it must not be read as a claim that the implemented Rust parser uses regex. This status update does not authorize overriding project rules or rewriting existing implementations. Start with the requested component and current evidence, not the old completion-provider starter task below.

---

## Historical C#-only context (retained reference)

Sections 1–5 preserve the original VSIX architecture and feature plan. Their present/future-tense wording is historical, not a current repository-wide inventory or instruction to rebuild completed PenguinCore work. Revalidate legacy feature claims before relying on them for the integrating VS bridge.

## 1. Project Overview

**PenguinExtension** is a lightweight, fast Visual Studio 2022 VSIX extension for Unreal Engine C++ developers written in C# (.NET Framework 4.7.2). It provides ReSharper/Rider-like developer productivity features (navigation, auto-completion, hover documentation, refactoring actions, and a Symbol Explorer) without the heavy overhead of full AST parsing.

It achieves speed through:
1. Regex-based parsing of Unreal macros (`UCLASS`, `USTRUCT`, `UENUM`, `UFUNCTION`, `UPROPERTY`, delegates) and their internal metadata specifiers.
2. A persistent SQLite cache (WAL mode).
3. Completely asynchronous background indexing that never blocks the Visual Studio UI.

**Tech Stack:**
- C# / .NET Framework 4.7.2
- Visual Studio Extensibility (VSSDK) / MEF (Managed Extensibility Framework)
- WPF (Windows Presentation Foundation) for custom tool windows
- SQLite (using WAL mode for concurrent read/writes)

## 2. Historical Target Features to Build/Extend

We are extending PenguinExtension beyond basic class/function indexing to include deep developer productivity features:

### A. Unreal Macro Specifier Autocompletion & Live Templates
Instead of just indexing symbols, the extension must provide intelligent autocompletion inside macro parentheses (`UPROPERTY(...)`, `UFUNCTION(...)`, `UCLASS(...)`).
* **Implementation Requirement:** Expand the `Completion/` MEF providers (`IAsyncCompletionSource`). When the caret is inside a macro definition, serve context-aware specifiers:
  * Inside `UPROPERTY()` -> Suggest: `EditAnywhere`, `VisibleAnywhere`, `BlueprintReadWrite`, `Category = ""`, `Transient`, `Replicated`.
  * Inside `UCLASS()` -> Suggest: `Blueprintable`, `Abstract`, `NotPlaceable`, `Config = Game`.
  * Inside `UFUNCTION()` -> Suggest: `BlueprintCallable`, `BlueprintPure`, `Server`, `Client`, `Reliable`.

### B. Deep "Go to Origin" Engine Navigation
Pressing `F12` (Go to Definition) or `Ctrl+Click` on `UCLASS`, `UPROPERTY`, or core Unreal types (like `AActor` or `FVector`) should navigate directly to the originating header file inside the engine source tree.
* **Implementation Requirement:** Expand `Services/UnrealProjectDetector` and `Services/UnrealIndexer` to optionally scan or path-resolve the local Unreal Engine install path (e.g., `Engine/Source/Runtime/...`). When a Go-To command is executed on a core macro or engine type, query `SQLiteCache` or resolve the file path directly and open it in the editor asynchronously.

### C. Regex/Text-Based C++ Refactoring Actions
Implement lightweight refactoring tools via smart commands or lightbulb actions without needing a full C++ AST:
* **Target Actions:**
  * **Generate Getters/Setters:** Given a selected `UPROPERTY`, generate encapsulated public `Get` and `Set` methods in the header and source files.
  * **Add Function Implementation:** If a function signature is added to a `.h` file, generate the boilerplate skeleton method in the corresponding `.cpp` file.

## 3. Directory Structure & Architecture

- **`PenguinExtensionPackage.cs`**: Main entry point (`AsyncPackage`). Handles asynchronous startup, loads cache, starts background indexer, and registers commands.
- **`Database/`**: SQLite persistence layer (`SQLiteCache.cs`). Stored in `.vs/PenguinExtension/penguin_cache.db`.
- **`Services/`**:
  - `UnrealProjectDetector`: Detects `.uproject` files and local UE engine paths.
  - `CacheService`: Thread-safe (`ReaderWriterLockSlim`) in-memory symbol cache.
  - `UnrealIndexer`: Throttled parallel regex-based scanner that parses headers and extracts classes, functions, properties, and macro specifiers.
  - `IncrementalIndexer`: Uses `FileSystemWatcher` to detect file saves and trigger single-file re-indexing.
- **`Completion/`**: MEF auto-completion providers implementing `IAsyncCompletionSource`. Hooks into modern VS IntelliSense for both code symbols and macro specifiers.
- **`QuickInfo/`**: Hover tooltip providers implementing `IAsyncQuickInfoSource`.
- **`UI/`**: Custom WPF tool windows (`UnrealExplorerWindow`), ViewModels, and XAML controls.
- **`Commands/`**: Visual Studio menu commands (Go To Definition, Refactoring commands) and their handlers.
- **`Models/`**: Data representations like `UnrealSymbol`, `IndexedFile`, `MacroSpecifier`, etc.

## 4. Legacy C# Coding Rules (component scope)

When contributing to or modifying this codebase, AI must follow these rules strictly:

### Rule 1: Never Block the UI Thread
Visual Studio extensions must be responsive. **Any** file I/O, database queries, or regex scanning must happen on background threads.
- **Use `JoinableTaskFactory`**: Use `ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync()` only when interacting with the VS UI (tool windows, active text buffers, registering commands).
- **Run Tasks on Background**: Use `Task.Run()` or explicitly switch (`await TaskScheduler.Default;`) for indexing or DB lookups.

### Rule 2: Thread-Safe State
Since indexing runs on a background thread and auto-completion/UI reads from the cache concurrently, the `CacheService` must maintain strict thread safety.
- Use `ReaderWriterLockSlim` or concurrent collections (`ConcurrentDictionary`, etc.) for shared state.
- SQLite is configured with WAL (Write-Ahead Logging) to allow concurrent reads while the background thread writes. Do not change this database mode.

### Rule 3: Fast Regex Parsing (No AST)
Do not attempt to introduce full C++ AST parsing (like Clang or Roslyn for C++). Speed comes from simple, heuristic regex parsing.
- When expanding `UnrealIndexer` to parse macro arguments or specifiers, ensure regex patterns remain pre-compiled (`RegexOptions.Compiled`) and performant. Avoid catastrophic backtracking.

### Rule 4: MEF and VSSDK Best Practices
- Follow modern VSSDK patterns (`IAsyncCompletionSource` instead of legacy `ICompletionSource`).
- Ensure MEF exports (`[Export]`, `[Import]`) are correctly attributed and use `[ContentType("C/C++")]`.

### Rule 5: Legacy .csproj
- New `.cs` files must be explicitly added to `PenguinExtention.csproj` as `<Compile>` items or they won't compile.
- The namespace is `PenguinExtention` (typo). Do not rename it — it's baked into the assembly, manifest, and package.

## 5. Historical Where to Start (superseded)
Please begin by writing or updating the `IAsyncCompletionSource` implementation inside the `Completion/` folder. It should detect when the user's text caret is inside the parentheses of a `UPROPERTY()` or `UFUNCTION()` macro and return an asynchronous list of completion items containing valid Unreal Engine specifiers (e.g., `EditAnywhere`, `BlueprintReadWrite`).
