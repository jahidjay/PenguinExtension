# PenguinExtension / PenguinCore — State Report

## Current status — 2026-10-06 (supersedes the historical gap analysis)

The repository is no longer the C#-only, untested snapshot audited below. **PenguinCore libraries, LSP/MCP servers, VS Code client and desktop implementation now exist and have recorded automated verification. The Visual Studio bridge has passed headless and package checks.** This is an implementation/verification status update, not a fresh source audit or a claim that every legacy defect was fixed.

**Measured-results authority:** [docs/verification.md](docs/verification.md). Its later component/integration entries supersede the initial baseline. The summary below cites the recorded executions in that ledger; it does not establish additional runtime acceptance.

| Area | Superseding status |
| --- | --- |
| Shared core | `ue-parser`, `ue-db`, `ue-core`, `ue-style` and `ue-ai` are implemented/tested. Rust reflection parsing and pooled WAL/session isolation are distinct from the historical C# regex/shared-connection path. |
| Headless adapters | `ue-lsp` and `ue-mcp` implemented/tested. The ledger records 265 unit/integration + 3 documentation tests = 268 passing tests, including 11 LSP and 11 MCP real-process tests; workspace Clippy/formatting checks passed. Nested lease-test repeats are not added to that total. |
| Windows server delivery | Release LSP/MCP executables produced; release and packaged LSP smoke checks passed as listed in the ledger. This is not an IDE acceptance pass. |
| VS Code | Implemented client; 27 unit tests, strict typecheck and production build passed. Windows-x64 VSIX inspection and extracted-server smoke passed. Live VS Code/Antigravity runtime acceptance is unverified. |
| Desktop | Implemented Tauri app; 24 frontend and 20 native tests plus recorded build/lint checks passed after source-read hardening. Windows executable and NSIS installer rebuilt with the fix, but installer not installed and GUI acceptance unverified. |
| Visual Studio | Implemented; 21 bridge/DTO assertions, VS2022/VS18 MSBuild builds, 71-entry VSIX inspection and extracted-server smoke check passed. This does not establish interactive compatibility; the manifest range stays `[17.0, 18.0)`. |
| Local AI | Bounded, explicit opt-in preview requests implemented/tested with loopback HTTP mocks only. No live-model pass; the default model was not installed for these checks. |
| Platform coverage | Only the ledger's measured host checks are established. Cross-platform intent/CI configuration does not verify Linux/macOS/ARM packages or runtime behavior. |

### Current gaps and acceptance boundaries

- Complete isolated VS runtime acceptance. The recorded bridge and package checks do not prove editor registration, tool-window behavior, shutdown in the IDE, or UI-thread responsiveness. Legacy remains the default, with Core available by explicit selection and no silent fallback.
- Perform [isolated manual acceptance](docs/manual-acceptance.md) before claiming GUI/IDE compatibility, installer behavior, external MCP-client acceptance or live-model functionality. Preserve normal IDE profiles; a checklist is not evidence of execution.
- Do not claim Rider parity. [LSP limitations](crates/ue-lsp/README.md) explicitly exclude general C++ type/member resolution, metadata-key completion inside `meta=(...)`, and automated refactoring/source writes. Reflected-symbol behavior is narrower than compiler semantics.
- Keep [core](crates/ue-core/README.md), [MCP](crates/ue-mcp/README.md), [VS Code](apps/vscode-ext/README.md), [desktop](apps/desktop-tauri/README.md), and [protocol](docs/penguin-protocol.md) boundaries explicit: LSP live buffers are not shared with disk-backed desktop/MCP sessions; AI previews are neither applied nor executed automatically.

### How to read the retained audit

The original analysis below is a **historical audit of the legacy C# implementation**, preserved for its concrete bug hypotheses, file references and proposed fixes. Its file/line numbers, repository counts, "works" claims, "zero tests", missing logging/features, and recommended priority order are **not current repository-wide facts**. They have not been re-audited here. Some concerns may remain relevant to legacy/fallback paths; check the current implementation before marking any individual finding open or fixed. Rust implementation/tests do not by themselves prove those old C# paths were repaired.

---

## Historical legacy VSIX audit (original snapshot; superseded)

> **Historical purpose:** a severity-ranked gap analysis of the plugin at the time of the original audit, intended as raw material for an improvement prompt. Findings are ordered by impact; every claim cites a file/line.
>
> **Historical review scope:** all materially important source reviewed (~4,300 of 4,968 lines across 20 of 34 files), including `.csproj`, `.vsct`, and the specifier catalog. Unread remainder is low-risk XAML/plumbing (`UnrealExplorerControl.xaml`, `ShortcutsReferenceControl.xaml(.cs)`, `UnrealExplorerWindow.cs`, `SymbolInspectorControl.xaml`, `AssemblyInfo.cs`, `PenguinExtensionCommandIds.cs`, both MEF providers, `UnrealSymbolKind.cs`, `IndexedFile.cs`).
>
> **Historical repo state (not current):** 3 commits. Working tree dirty — 8 modified, 11 untracked. **Zero test files.**

---

## 1. Historical Snapshot

| Area | State |
|---|---|
| Symbol indexing (regex + SQLite + parallel scan) | Works, but O(n²) hot paths, broken nested-paren parsing, DB concurrency bug |
| Completion (symbols + macro specifiers) | Works for globals; **no member scoping after `::` `->` `.`** — the main distance from Rider |
| QuickInfo hover | Works; fake-async, usage-count pollution, data race on inheritance chain |
| Go To Definition | Works for first match; promised disambiguation picker **does not exist** |
| Generate Implementation / Getter-Setter | **Fails on idiomatic UE formatting** (macro on its own line); wrong class names; no undo |
| Unreal Explorer / Symbol Inspector | Works; async-void crash risk, polling timer, filter-after-limit bug, event leak |
| Options page | **2 of 5 settings are dead** (never read) |
| Build / packaging | Hard Rule 5 satisfied; machine-specific NuGet paths are the fragility |
| Tests | **None** |
| Logging / diagnostics | **None** — ~15 bare `catch { }` blocks swallow everything silently |

---

## 2. Critical (correctness / crash / corruption)

### 2.1 Shared `SQLiteConnection` used concurrently — `Database/SQLiteCache.cs`

The single most serious defect. Writes serialize on a `SemaphoreSlim(1,1)` (`_writeLock`), but **reads take no lock at all**: `LoadAllSymbolsAsync`, `SearchSymbolsAsync`, `GetSymbolsByNameAsync`, `GetIndexedFileAsync`, `GetSymbolCountAsync` all issue commands on the one `_connection`.

`GetIndexedFileAsync` is called per-file from `_maxParallelism` concurrent indexing tasks (`UnrealIndexer.cs:188`). `System.Data.SQLite` connections are not safe for concurrent commands → crash/corruption risk under load. WAL mode exists precisely to allow multi-**connection** concurrency, which this code never uses.

**Constraint:** the fix must keep WAL mode (Hard Rule 2) — use per-operation or pooled connections, or serialize reads. Do not change the journal mode.

### 2.2 DB disposed while indexing still runs — `PenguinExtensionPackage.cs:112-145, 241-251`

The full-index task is fire-and-forget (`_ = Task.Run(...)`) and never stored. `Dispose` cancels the CTS then immediately calls `_db?.Dispose()` — the shared connection can be disposed while in-flight indexing tasks still use it. `SQLiteCache.Dispose` also doesn't drain the write semaphore, and `_disposed` is never checked by other methods.

### 2.3 `InheritanceChain` data race — 1 writer, 3 readers

`CacheService.BuildInheritanceChains()` mutates shared `UnrealClassInfo` objects **outside the RW lock** (`kvp.Value.InheritanceChain = chain;`) while three unsynchronized readers walk the list:

- `QuickInfo/UnrealQuickInfoSource.cs:97-99`
- `Completion/UnrealCompletionSource.cs:263-268`
- `UI/SymbolInspectorControl.xaml.cs:95-97`

The field is `public List<string> InheritanceChain { get; set; } = new List<string>();` — a mutable list with a public setter, initialized non-null. Because it's never null, readers' `?.Count > 0` guards cannot protect against a **concurrently replaced** list; `string.Join` can observe a torn/half-built instance.

It also **re-runs in full on every single-file update** — O(classes × depth) churn per file during an engine index (~50k classes).

*Fix direction:* `IReadOnlyList<string>` with an atomic whole-list swap, or build under the write lock; and make single-file updates incremental.

### 2.4 All macro regexes break on nested parentheses — `Services/UnrealIndexer.cs:44-70`

Every macro pattern captures the meta block with `([^)]*)` — so `UPROPERTY(EditAnywhere, meta=(ClampMin="0.0"))` stops at the first `)`, mis-capturing the specifiers and often failing the whole match (the type/name groups then misalign). `RxUFunction`'s parameter group `\(([^)]*)\)` likewise breaks on default args containing parens.

`meta=(...)` is ubiquitous in real UE code — this corrupts a meaningful fraction of the index. The same `([^)]*)` flaw exists in `GenerateImplementationCommand.RxFuncDecl:23-27` and `GenerateGetterSetterCommand.RxUproperty:24`.

### 2.5 Generate Implementation emits non-compiling code — `Commands/GenerateImplementationCommand.cs`

- **Class name from filename** (`:91`): `Path.GetFileNameWithoutExtension(bufferPath)` → in `Character.h` declaring `ACharacter`, generates `Character::BeginPlay`. Wrong for essentially every UE class (A/U/F/E prefixes).
- **Single-line parse only** (`:62`): `GetTextStream(line, 0, line, 1000, …)` — the idiomatic UE layout (`UFUNCTION(...)` on its own line above the signature) and wrapped parameter lists cannot be parsed at all, even though the regex optionally accepts a same-line `UFUNCTION(...)` prefix.
- **`PURE_VIRTUAL(...)` leaks into the emitted signature** (`:96-97`): only `override`/`final` are stripped, by substring-replace. The comment at `:95` claims PURE_VIRTUAL is stripped — it isn't. (`virtual` never reaches group 4 because the regex consumes it non-capturing.)
- **Blind `File.AppendAllText`** (`:114`): writes the `.cpp` on disk behind VS's back — no duplicate detection (running twice = duplicate-symbol link error), **no VS undo unit** (Ctrl+Z won't revert), and it clobbers unsaved editor changes. Sync file I/O on the UI thread (Hard Rule 1 violation).
- Caret placement is a hard-coded `totalLines - 3` offset (`:129`).

### 2.6 Generate Getter/Setter fails on standard UE style — `Commands/GenerateGetterSetterCommand.cs`

- Regex is `^`-anchored requiring `UPROPERTY(...)` **on the same line** as the member (`:24`) — the overwhelmingly common UE style puts it on the previous line, so the command simply fails.
- `bIsReady` → `GetIsReady()` (`:90-91`); UE convention is `IsReady()`. The `m_` branch (`:92-93`) isn't a UE convention at all.
- Hand-maintained `PrimitiveTypes` set (`:28-36`) can never cover UE's type surface; `TObjectPtr<T>` wrongly becomes `const TObjectPtr<T>&` instead of by-value.
- Inserts at the **end of the property line** (`:115`) → accessors land inside whatever access section the member is in (usually `private:`/`protected:`). No access-specifier awareness, no duplicate detection, no undo unit.
- `Marshal.StringToCoTaskMemAuto` (`:112`) with `ReplaceLines` — contract calls for `StringToCoTaskMemUni`; works today only incidentally on .NET Framework/Windows.

### 2.7 `async void` surfaces — process-crash risk

- `UI/UnrealExplorerViewModel.cs` — `private async void DebouncedSearch()`. Any exception other than `TaskCanceledException` crashes devenv. Its `ExecuteSearch` then marshals with a **blocking** `Application.Current?.Dispatcher?.Invoke` from a pool thread (deadlock risk with JTF), and the `?.` silently skips the UI update when `Application.Current` is null. `_searchCts` is replaced without disposal or synchronization.
- `PenguinExtensionPackage.cs:192-202` — tool-window commands registered with an `async (s, e) => …` lambda as a `MenuCommand` handler: another async-void.

### 2.8 Class-info can bind to the wrong file's symbol — `Services/CacheService.cs` (`UpdateSymbolsAsync`)

ID resolution uses `GetSymbolsByNameAsync(ci.ClassName).FirstOrDefault()`, which is **not file-scoped** — a same-named symbol in a different file can receive this file's class_info. It also pre-queries only `newClassInfos[0]`, then does one extra DB round-trip per remaining class. The whole delete → insert → class-info-write sequence isn't wrapped in a spanning transaction, and the memory swap happens after, so a mid-sequence failure leaves DB and memory inconsistent.

---

## 3. High (performance / UX-defining gaps)

### 3.1 No member-scoped completion — `Completion/UnrealCompletionSource.cs:126-140, 181-182`

After `::`, `->`, `.` the code only resets `identStart = column`; it never resolves the LHS type or scopes to its members:

```csharp
var prefix = applicableToSpan.GetText();
var symbols = cache.Search(prefix, kindFilter: null, limit: 100);
```

`MyActor->` returns the same global 100 symbols as a bare identifier. This is the single biggest functional distance from Rider. The data to do better already exists (`OwnerClass` is indexed) — a first increment is filtering by `OwnerClass` plus the inheritance chain.

### 3.2 O(n²) indexing hot paths — `Services/UnrealIndexer.cs`

- `GetLineNumber` (`:456-464`) scans from offset 0 for **every match** → O(n²) per file. Precompute line-start offsets once and binary-search.
- `FindEnclosingClass` (`:467-478`) re-runs **three full-file regexes plus a LINQ sort per UFUNCTION/UPROPERTY match**, and has no brace-depth tracking — members after a class's closing brace are misattributed to it. Match classes once per file, then binary-search by offset.
- `ExtractClassInfos` (`:366-402`) runs the same three regexes a **third** time per file. `IsAbstract = m.Groups[1].Value.Contains("Abstract")` (`:377`) is a naive case-sensitive substring test.

### 3.3 Startup hydration: ~14 `GetOrdinal` calls per row — `Database/SQLiteCache.cs` (`ReadSymbol`)

For a 200k-symbol cache that's ~2.8M string-keyed ordinal lookups on the startup path. Hoist ordinals once per reader. This is the main lever on the (unmeasured) "under 2 seconds" claim in `StartupCacheLoader.cs`.

### 3.4 Per-file progress → UI-thread flood — `UnrealIndexer.cs:150` + `PenguinExtensionPackage.cs:95-110`

`progress?.Report(progressData)` fires once per file, passing a **shared mutated object** (reads race with the `lock (progressData)` writer), and the package handler marshals every report to the UI thread for a status-bar update via `JoinableTaskFactory.RunAsync` + `SwitchToMainThreadAsync`. An engine index ≈ 100k files ≈ 100k UI-thread hops — the most user-visible startup perf defect. Throttle (every ~250 ms or every N files) and report immutable snapshots.

### 3.5 Camel-hump fallback linear-scans all names per keystroke — `Services/CacheService.cs` (`Search`)

When prefix results are sparse it walks `_sortedNames` end-to-end **under the read lock on the completion keystroke path**. `HumpMatch` is also unbounded-recursive, and its first-char rule requires matching at index 0 — so `"Char"` never matches `"ACharacter"`, defeating UE's A/U/F/E prefix convention.

*Note:* camel-hump matching **does** exist (here in `CacheService`, not in the completion source) — this corrects any claim that fuzzy matching is missing. The problems are cost and the offset rule.

### 3.6 Symbol Inspector polls the caret at 2 Hz forever — `UI/SymbolInspectorControl.xaml.cs:28-44`

A 500 ms `DispatcherTimer` runs `GetWordUnderCaret()` — `GetGlobalService(SVsTextManager)` → `GetActiveView` → `GetCaretPos` → `GetTextStream` — on the UI thread twice per second while the panel is open, even when VS is idle. The comment states the rationale: `// ponytail: poll every 500ms instead of hooking caret events`.

*Precision:* the `if (word == _lastWord) return;` guard at `:43` means the cache read lock is taken only on word **change**. The unconditional 2 Hz cost is the UI-thread COM caret read, not cache contention. Replace with caret-moved events via a MEF text-view listener.

### 3.7 `DetectAsync` runs almost entirely on the UI thread — `Services/UnrealProjectDetector.cs`

`GetSolutionDirectoryAsync` switches to the main thread and `.ConfigureAwait(true)` keeps the rest of `DetectAsync` there: directory walks, the 4- and 6-level parent probes, `File.ReadAllText(UProjectFilePath)`, JSON parse, and **registry opens** all execute on the UI thread. The `await Task.Yield(); // ensure we're off the UI thread` is ineffective, and the method's own XML doc ("must be called on a background thread") is contradicted by the code. Hard Rule 1 violation on the startup path.

### 3.8 UI-thread cache access + sync I/O in Go To Definition — `Commands/GoToUnrealDefinitionCommand.cs`

`Execute` asserts `ThrowIfNotOnUIThread()` (`:43`) then runs `cache.GetByExactName` (`:61`), `cache.RecordUsage` (`:81`), and `File.Exists` (`:90`) on the UI thread. Also: 500-column `GetTextStream` cap (`:128`); the XML doc promises a disambiguation picker (`:15`) but the code takes the first match (`:79`, `// For now, just go to the first match`); `TryNavigateToEngineMacro` opens `ObjectMacros.h` at the **top of the file** rather than the macro's line, from a hard-coded partial macro list (`:163-170`, `:195-196`).

### 3.9 Incremental indexer reliability — `Services/IncrementalIndexer.cs`

- `EnableRaisingEvents = true` set in the initializer (`:46`) **before** `Filter` (`:50`) — a brief unfiltered-event window.
- **No `Error` handler and no `InternalBufferSize` bump** → a large rebuild overflows the watcher buffer and changes are silently lost with no recovery re-scan.
- **Debounce starvation:** the timer resets on every change (`:96`), so continuous churn can prevent it from ever firing.
- **Drain race:** snapshot `_pendingChanges.Keys.ToArray()` then `TryRemove` each — changes arriving in between are lost.
- No coordination with a still-running full index → both hit the shared connection (see 2.1).
- Fire-and-forget tasks at `:75`, `:89`, `:109`; `catch { /* log and continue */ }` (`:120`) logs nothing. `_disposed` not checked in the event handlers.

### 3.10 Explorer filters applied after the query limit — `UI/UnrealExplorerViewModel.cs`

`GetAllSymbols(limit: 200)` (empty search) or `Search(_searchText, limit: 500)`, then `.Where(filters).Take(300)` — so unchecking "Engine" *shrinks* results instead of surfacing more project symbols. Also: `Clear()` + per-item `Add` → up to 300 `CollectionChanged` events per keystroke; `CacheReady` subscribed in the ctor and **never unsubscribed** (the singleton keeps the ViewModel alive across close/reopen); and if the tool window is restored before cache init completes, `if (CacheService.Instance != null)` fails, the event is never subscribed, and the Explorer stays "Initializing…" forever.

---

## 4. Medium

| # | Finding | Where |
|---|---|---|
| 4.1 | `UpsertSymbolsBatchAsync` is **not an upsert** — plain `INSERT`, no `ON CONFLICT`, no unique constraint on `(name, file_path, line_number, kind)`. Correctness depends entirely on callers deleting by file first. Inconsistent with `UpsertClassInfoBatchAsync` / `UpsertIndexedFileAsync`, which use `INSERT OR REPLACE`. | `Database/SQLiteCache.cs` |
| 4.2 | **Usage counts destroyed on every re-index:** `PRAGMA foreign_keys = ON` + `symbol_usage … ON DELETE CASCADE` means `DeleteSymbolsByFileAsync` silently wipes the `AccessCount` ranking data. | `Database/SQLiteCache.cs` |
| 4.3 | **No schema-version table → no migration path.** `InitializeAsync` has no try/catch; `StartupCacheLoader` has none either, so a corrupt DB propagates to the package's top-level catch and silently kills indexing. | `Database/SQLiteCache.cs`, `Services/StartupCacheLoader.cs` |
| 4.4 | Blocking `cmd.ExecuteNonQuery()` per row inside async batch loops, within a sync `BeginTransaction()`; no `Rollback`, no catch, no logging. | `Database/SQLiteCache.cs` |
| 4.5 | Culture-sensitive date round-trip: written `.ToString("o")`, read `DateTime.Parse(...)` with no `InvariantCulture` / `RoundtripKind`. | `Database/SQLiteCache.cs` |
| 4.6 | A file whose symbols all disappear is never cleaned: `if (symbols.Count > 0)` gates the update, so stale entries persist. | `Services/UnrealIndexer.cs:196-199` |
| 4.7 | Sync `File.ReadAllText` inside the async per-file method, hard-coded UTF8 (garbles non-UTF8/BOM'd sources), bare `catch { return false; }` hides unreadable files. Hash computed only after the full read. | `Services/UnrealIndexer.cs:176-185` |
| 4.8 | Unbounded `List<Task>` — one task allocated per file (100k+ for an engine index). Use chunking or a fixed worker pool. No cancellation checks inside per-file work. | `Services/UnrealIndexer.cs:129` |
| 4.9 | `GetDialogPage` called after `await TaskScheduler.Default` — a main-thread-affinitized shell call from a background thread. | `PenguinExtensionPackage.cs:64, 76` |
| 4.10 | QuickInfo: `async` with **zero awaits** (all work on the caller's thread, CS1998); `cancellationToken` never used; records usage for **all** matches on **every hover** (write storm + ranking pollution); the `────` divider is appended after the last overload too. | `QuickInfo/UnrealQuickInfoSource.cs:58, 136, 154-157` |
| 4.11 | Unsafe singletons: `CacheService.Initialize` and `UnrealProjectDetector.CreateInstance` assign `_instance` with no `Interlocked` or double-init guard. Detector state is `private set`, non-volatile, yet read from indexing threads. | `Services/CacheService.cs`, `Services/UnrealProjectDetector.cs` |
| 4.12 | `CacheService`: `IsLoaded` non-volatile; `SymbolCount` reads `_allSymbols` with **no read lock** while `ReplaceFileSymbols` mutates under the write lock; `GetAllSymbols` LINQ-filters then `Take(limit)` with no ordering (arbitrary 1000); no eviction, unbounded memory. `RecordUsage`'s `IncrementAccess()` **is** `Interlocked` — so this is a contract smell, not a lost-update bug — but the plain `AccessCount` setter on the hydration path isn't, and the fire-and-forget DB write swallows failures. | `Services/CacheService.cs`, `Models/UnrealSymbol.cs` |
| 4.13 | **Keybindings are global, not editor-scoped:** all six `<KeyBinding>` entries use `editor="guidVSStd97"`, so every chord is live everywhere in VS. `Ctrl+Shift+U` also collides with VS's built-in **Edit.MakeUpperCase** in the text editor. | `PenguinExtention.vsct:103-139` |
| 4.14 | **No `<Icon>`/`<Bitmap>` elements** → commands render iconless. No `<VisibilityConstraints>`, and registration uses plain `MenuCommand` not `OleMenuCommand`, so there's no `BeforeQueryStatus` — the editor-dependent commands are **always enabled**, even with no text view open or in a non-C++ file. The `:141` comment "must match `PenguinExtensionCommandIds.cs`" documents a manual-sync hazard with no build-time verification. | `PenguinExtention.vsct`, `PenguinExtensionPackage.cs` |
| 4.15 | **`.csproj` hard-codes machine-specific NuGet paths** — `VSToolsPath` fallback pinned to `microsoft.vssdk.buildtools\17.9.3168`, and both `SQLite.Interop.dll` `<Content>` items pulled from `$(USERPROFILE)\.nuget\packages\stub.system.data.sqlite.core.netframework\1.0.119\...` (a transitive package whose version is pinned nowhere in `<PackageReference>`). Breaks on CI, a relocated `NUGET_PACKAGES`, or any package bump — the exact area that already broke once (commit `e09bf15`). Use `$(NuGetPackageRoot)` + version properties. | `PenguinExtention.csproj:7, 176-185` |
| 4.16 | Packaging extras: the **x86 interop DLL is dead weight** (VS2022 is x64-only); `<RuntimeIdentifier>win</RuntimeIdentifier>` is unusual on a legacy Framework VSIX; `NoWarn="NU1604"` suppresses missing-lower-bound warnings; no analyzers; no test project. *(Hard Rule 5 itself is fully satisfied — all 28 code files plus 3 `<Page>` XAML entries are explicitly listed with correct `DependentUpon` wiring.)* | `PenguinExtention.csproj` |
| 4.17 | `[ProvideAutoLoad(SolutionExistsAndFullyLoaded)]` loads on **every** solution; the non-UE path bails but never re-detects if a `.uproject` appears later. Multiple `.uproject` files are resolved by `FirstOrDefault()`. | `PenguinExtensionPackage.cs`, `Services/UnrealProjectDetector.cs` |
| 4.18 | Specifier catalog gaps: **duplicate `NoClear` entry** in the UPROPERTY list (`:44` and `:51`, with different descriptions — renders twice in completion); **`UINTERFACE` and `UDELEGATE` absent** from `MacroNames`/`GetSpecifiers` despite the indexer emitting those symbols; and **no completion at all inside `meta=(...)`** — `DetectMacroContext` breaks at the first unmatched `(`, resolves the word `meta`, which isn't in `MacroNames`, and returns null. That's where most UPROPERTY authoring happens. Catalog is also shallow vs. real UE (UProperty 27, UFunction 16, UClass 17, UStruct 7, UEnum 3), missing `AssetRegistrySearchable`, `DuplicateTransient`, `EditFixedSize`, `Getter`/`Setter`, `SealedEvent`, `BlueprintGetter`/`BlueprintSetter`, `Deprecated`, `AdvancedClassDisplay`, and more. | `Completion/UnrealMacroSpecifiers.cs` |
| 4.19 | UFUNCTION/UPROPERTY specifier text **is captured** by the indexer (group 1) then **discarded** — `UnrealSymbol` has no field for it, so member specifiers never reach hover or the inspector. `UnrealClassInfo.Module` is declared, documented, and never populated (dead field). `MetaSpecifiers` is a raw unparsed comma string — which is why `IsAbstract` has to substring-match. `EngineVersion` is set only on the `.uproject`-association path, so it stays null for source builds. | `Services/UnrealIndexer.cs`, `Models/UnrealSymbol.cs`, `Models/UnrealClassInfo.cs`, `Services/UnrealProjectDetector.cs` |
| 4.20 | No UENUM enumerator extraction despite `UnrealSymbolKind.EnumValue` existing — the Explorer's EnumValue filter is inert. `EnumerateSourceFiles` also skips `ThirdParty` (possibly over-aggressive), accepts only `.h`/`.hpp`/`.cpp` (no `.inl`/`.hxx`), and filters `.generated.h` but not `.gen.cpp`. | `Services/UnrealIndexer.cs:407-453` |
| 4.21 | Completion polish: `DetectMacroContext` runs **twice per keystroke** (`:87`, `:168`); its 500-char backward scan drops long meta blocks (`:306`); `filters: ImmutableArray<CompletionFilter>.Empty` on every item → **no filter buttons** despite per-kind icons; ranking is only project-before-engine (`:193`); hard-coded image catalog GUID instead of `KnownMonikers` (`:25`); `GetDescriptionAsync` is fully synchronous; **no `IAsyncCompletionCommitManager`** → no commit chars and no caret repositioning, so `Category = ""` and `meta = ()` leave the caret outside the quotes/parens; fragile `&&`/`||` precedence at `:335`. | `Completion/UnrealCompletionSource.cs` |
| 4.22 | **Dead user settings:** `EnableHoverInfo` and `EnableCompletionSuggestions` are never read by QuickInfo or completion — 2 of 5 options do nothing. `MaxIndexingThreads` defaults to `Environment.ProcessorCount / 2`, which yields 1 on a 2-core machine and 0 before clamping. No options for `.inl`/`.hxx`, ThirdParty inclusion, completion limit, debounce interval, or cache clear/rebuild. | `Services/PenguinOptionsPage.cs`, `PenguinExtensionPackage.cs:106` |
| 4.23 | `SearchSymbolsAsync` (the DB fallback) is **likely dead code** — completion bails when `!cache.IsLoaded`, so it's never exercised. It also uses unescaped `LIKE @prefix` (a typed `_` becomes a wildcard) and won't hit `idx_symbols_name` without `COLLATE NOCASE`. No FTS5, no `class_info` index, no index on `indexed_files.content_hash`. | `Database/SQLiteCache.cs` |

---

## 5. Low / hygiene

- **20+ `ponytail:` scratch markers across ≥10 files** — e.g. `UnrealCompletionSource.cs:90, 296, 301, 360`; `GenerateImplementationCommand.cs:15, 21, 82, 95, 108`; `GenerateGetterSetterCommand.cs:16, 22, 27, 87, 97, 111`; `SymbolInspectorControl.xaml.cs:16, 28, 69, 180, 206`; `GoToUnrealDefinitionCommand.cs:64, 162`; `PenguinExtensionPackage.cs:191`; `UnrealMacroSpecifiers.cs:7`. Leftover scaffolding — sweep them.
- **~15 bare `catch { }` / comment-only catches** with no logging anywhere in the codebase. Combined with zero telemetry, failures are completely invisible. One shared output-pane logger would pay for itself immediately.
- `WriteToOutputWindow` calls `CreatePane` on **every** message; hard-coded pane GUID (`PenguinExtensionPackage.cs:230-231`). `RegisterCommandsAsync` silently returns if `commandService == null` (`:167`). `GoToUnrealDefinitionCommand` never null-checks `commandService` before `AddCommand` (`:28`, `:37-38`) — unlike `GenerateGetterSetterCommand`, which does (`:54`).
- **Dead code:** `RxPlainClass` and `RxDocComment` (`UnrealIndexer.cs:78-85`), `var lines = content.Split('\n');` (`:218`) plus stray blank lines `:220-222`, `StartupCacheLoader.HasExistingCache`, `GoToUnrealDefinitionCommand.Instance` (assigned, never read), and the dead `reader.GetOrdinal(...) >= 0` guard in `ReadSymbol` (`GetOrdinal` throws rather than returning -1).
- **Ignores VS theming:** hard-coded `Brushes.DarkSlateBlue` / `DarkGreen` / `White` for the ENGINE/PROJECT badge (`SymbolInspectorControl.xaml.cs:156-159`) — clashes in Light theme, contrast/accessibility issue. Use `VsBrushes`/theme keys. Emoji glyphs (📁 📦 🔗 📝 ⚙) in completion and hover tooltips render inconsistently.
- `KindGlyph` collisions (`Models/UnrealSymbol.cs`): Class/Struct/Interface all share `""` and Function/Delegate share `""` — the Explorer can't visually distinguish those kinds, and it requires the Segoe MDL2 Assets font. `KindDisplay => Kind.ToString()` is the raw enum name, not a friendly label.
- `LocationText_Click` (`SymbolInspectorControl.xaml.cs:176-202`) uses `ServiceProvider.GlobalProvider` instead of the package, is fire-and-forget with `catch { }` (`:200`), and does no `File.Exists` check before `OpenDocument`.
- `GetWordUnderCaret` returns null when the caret sits at end-of-line (`col >= lineText.Length`, `:224`).
- **Magic numbers throughout, none configurable:** 500-char scan windows and column caps, `limit: 100`, `Take(3)`, 150 ms search debounce, 500 ms file debounce, 500 ms poll interval, walk depths 4 and 6.
- `RelayCommand<T>.CanExecute` does an unguarded `(T)parameter` cast.
- `Specifier` carries only `Name`/`Description`/`InsertText` — no arity or value-type info, so flag vs. key=value is encoded only in hand-written `InsertText`.
- Cache folder is `.vs/PenguinExtension` (correct spelling) while the namespace is `PenguinExtention` (pinned typo — the namespace must stay per Hard Rule 6; just be aware of the mismatch).
- Registry probing uses a deliberate but undocumented key-spelling difference: `HKLM\SOFTWARE\EpicGames\Unreal Engine` vs. `HKCU\SOFTWARE\Epic Games\Unreal Engine\Builds`.

---

## 6. Doc drift

- `CLAUDE.md`'s directory map omits `UI/UnrealExplorerViewModel.cs`, `UI/UnrealExplorerControl.xaml`, and `UI/UnrealExplorerControl.xaml.cs` — all three exist on disk and **are** correctly listed in the `.csproj`, so the drift is in the doc, not the build.
- `GoToUnrealDefinitionCommand` XML doc promises a multi-match disambiguation picker that doesn't exist.
- `GenerateImplementationCommand` header claims it "handles modern C++ (auto, constexpr, noexcept, templates, override)" — the regex handles none of those meaningfully; the `:95` comment claims `PURE_VIRTUAL` is stripped — it isn't.
- `UnrealProjectDetector.DetectAsync` doc says "must be called on a background thread (uses file I/O)" — the code pins itself to the UI thread.
- `StartupCacheLoader` doc claims an "under 2 seconds" target — never measured, asserted, or reported.
- `IncrementalIndexer`'s catch comment says "log and continue" — nothing logs.

---

## 7. Historical Missing vs. Rider parity (revalidate against current components)

1. **Member-scoped completion** after `::` / `->` / `.` (see 3.1) — highest-value gap.
2. **`meta=(...)` sub-specifier completion** (`ClampMin`, `ClampMax`, `UIMin`, `UIMax`, `EditCondition`, `DisplayName`, `ToolTip`, `AllowPrivateAccess`, `GetOptions`…) plus UPARAM/UINTERFACE/UDELEGATE lists.
3. Find All References / peek definition; symbol rename.
4. Multi-match disambiguation picker for Go To Definition.
5. UENUM enumerator indexing (`EnumValue` kind exists, unused).
6. `.inl`/`.hxx` indexing; options for ThirdParty inclusion, result limits, debounce, cache clear/rebuild.
7. A commit manager for completion (caret inside `""` for `Category = ""`, custom commit chars, snippet-style parameter insertion).
8. **Any test suite** — parsing fixtures for the regexes would catch most of §2.4–2.6 instantly — and **any logging/diagnostics channel**.

---

## 8. Historical suggested priority order (not the current backlog)

1. **Stability first:** SQLite connection concurrency (2.1), dispose ordering (2.2), the `InheritanceChain` race (2.3), and the two `async void` handlers (2.7).
2. **Index correctness:** one balanced-paren macro matcher shared by the indexer and both Generate commands (2.4); brace-aware `FindEnclosingClass`; a line-offset table (3.2); file-scoped class-info binding (2.8); clear symbols when a file empties (4.6).
3. **Code-gen correctness:** multi-line declaration parsing, cache-resolved class names, duplicate detection, and VS buffer edits inside proper undo units (2.5, 2.6).
4. **Performance:** throttled progress reporting (3.4), hoisted ordinals (3.3), bounded camel-hump with offset-anchored matching (3.5), event-driven inspector (3.6), background-thread detector (3.7).
5. **Feature depth:** member-scoped completion (3.1), `meta=()` sub-specifiers (4.18), wire up the two dead options (4.22), fix Explorer filter-after-limit (3.10).
6. **Hygiene:** a real logging channel, remove `ponytail:` markers and dead code, theme-aware brushes, editor-scoped keybindings plus a fix for the `Ctrl+Shift+U` / MakeUpperCase collision (4.13), command icons and `BeforeQueryStatus` (4.14), portable `.csproj` paths (4.15), and sync the docs (§6).

**Original legacy C# constraints (from `CLAUDE.md`; not a description of the Rust implementation):** never block the UI thread; keep WAL mode and `ReaderWriterLockSlim` in `CacheService`; no C++ AST parsing (compiled regex only, no catastrophic backtracking); MEF content type stays `C/C++`; new files must be added as `<Compile>` items in the legacy `.csproj`; the `PenguinExtention` namespace typo stays.
