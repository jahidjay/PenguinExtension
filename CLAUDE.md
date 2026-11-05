# CLAUDE.md — PenguinExtension

This file provides context for Claude Code, Cursor, and other AI assistants working on this codebase.

## What This Is

A Visual Studio 2022 VSIX extension (C# / .NET Framework 4.7.2) that gives Unreal Engine C++ developers Rider-like productivity features: autocomplete, hover docs, symbol navigation, macro specifier completion, refactoring commands, and an explorer window. No AST parsing — speed comes from regex + SQLite + async background indexing.

## Tech Stack

- **C# / .NET Framework 4.7.2** — legacy-style `.csproj` (not SDK-style)
- **VSSDK 17.x** — `AsyncPackage`, MEF exports, `IAsyncCompletionSource`, `IAsyncQuickInfoSource`
- **WPF** — tool windows with MVVM
- **SQLite** (WAL mode) — persistent symbol cache in `.vs/PenguinExtension/penguin_cache.db`
- **NuGet**: `Microsoft.VisualStudio.SDK`, `Microsoft.VSSDK.BuildTools`, `System.Data.SQLite.Core`, `Newtonsoft.Json`

## Directory Map

```
PenguinExtensionPackage.cs   — AsyncPackage entry point (startup orchestration)
Commands/
  GoToUnrealDefinitionCommand.cs  — Navigate to symbol + engine macro fallback
  GenerateImplementationCommand.cs — Generate .cpp skeleton from .h declaration
  GenerateGetterSetterCommand.cs   — Generate Get/Set from UPROPERTY
  PenguinExtensionCommandIds.cs    — GUID + IDs (sync with .vsct)
  PenguinEditor.cs                 — Shared editor helpers; multi-file edits in one linked undo
  NavigationCommands.cs            — Find Symbol, Open File, Toggle Header/Source, Go To Implementation/Related, List Methods
  RefactorCommands.cs              — Find References, Rename (+CoreRedirects), Add Include, Create Declaration, Move Impl, Encapsulate, Document
  CodeGenCommands.cs               — Snippets/Surround With, Implement Virtuals, Add Missing Cases, Introduce Variable, Forward Decl
Editor/                      — Classifier (UE coloring), reference-highlight tagger, signature help
  UnrealClassifier.cs              — Unreal syntax coloring: types/structs/enums/delegates/functions/properties + specifiers/meta keys;
                                     unindexed types colored from context (class/struct/enum keyword, U/A/F/E… prefix in a type position)
Completion/
  UnrealCompletionSource.cs         — IAsyncCompletionSource: symbols + macro specifiers
  UnrealCompletionSourceProvider.cs — MEF factory
  UnrealMacroSpecifiers.cs          — Specifier lookup (data comes from packs via PackService)
  CSharpPackCompletion.cs           — C# ("CSharp") completion + commit manager: pack snippets, Unity message stubs
  CSharpPackContext.cs              — Regex/brace-count C# context: enclosing class, base type, member level, snippet expansion
Database/SQLiteCache.cs      — SQLite persistence layer (WAL, concurrent R/W)
Models/                      — UnrealSymbol, UnrealClassInfo, UnrealSymbolKind enum, IndexedFile, PackManifest
QuickInfo/                   — IAsyncQuickInfoSource hover tooltips (CSharpPackQuickInfo: Unity message docs on hover)
Services/
  CacheService.cs            — Thread-safe in-memory cache (ReaderWriterLockSlim)
  UnrealIndexer.cs           — Parallel regex scanner for UE macros in .h/.hpp files
  UnrealProjectDetector.cs   — Finds .uproject, engine path (registry + options override)
  IncrementalIndexer.cs      — FileSystemWatcher + debounce for live re-indexing
  StartupCacheLoader.cs      — Hydrates CacheService from SQLite at startup
  OccurrenceScanner.cs       — Linear comment/string-aware identifier scan (references; no regex)
  AcronymIndex.cs            — Camel-hump/acronym keys (CMC → UCharacterMovementComponent)
  HeaderSourceResolver.cs    — .h ↔ .cpp incl. Public/Private; module-relative include paths
  SolutionFileIndex.cs       — Background project file list for Open File
  PenguinOptionsPage.cs      — Tools → Options page
  PackService.cs             — Penguin Packs: discover/detect/enable packs, serve specifiers, snippets, callbacks, index roots
  PackImporter.cs            — Import pack folder/zip or wrap a library folder in a new pack
  UnityEditorLocator.cs      — Unity version (ProjectVersion.txt), editor install (Hub/csproj/legacy), docs version
  McpClient.cs / McpConfig.cs / McpHub.cs — MCP client (stdio, streamable HTTP, SSE), .mcp.json servers, open connections
  PenguinLinks.cs            — Donate URL (one constant) + opener
  ThemeModel.cs / ThemeService.cs — Penguin Color Theme: entries + defaults, theme.json, runtime apply (SYNTAX_COLOR_PLAN.md)
UI/
  UnrealExplorerWindow.cs          — Symbol Explorer tool window
  SymbolInspectorWindow.cs         — Live symbol documentation panel
  SymbolInspectorControl.xaml/.cs  — WPF control for inspector
  ShortcutsReferenceWindow.cs      — Keyboard shortcuts reference
  ShortcutsReferenceControl.xaml/.cs — WPF control for shortcuts
  ColorThemeWindow.cs              — Penguin Color Theme window (code-built WPF)
  PacksWindow.cs                   — Penguin Packs window (check marks, import, remove)
  McpWindow.cs                     — Penguin MCP window (servers, tools, call)
  DefinitionPreviewWindow.cs       — Live declaration/implementation preview (Alt+U, W)
  FindReferencesWindow.cs          — Find References results (Alt+U, F)
  QuickPickDialog.cs / RenameDialog.cs — Fuzzy picker + rename preview dialogs
```

## Keyboard Shortcuts (all two-chord: Alt+U, then… — Ctrl+Shift+U also works, but NOT inside the text editor)

Ctrl+Shift+U is `Edit.MakeUppercase` in the Text Editor scope, which beats Global bindings, so the
Ctrl+Shift+U chords never fire while editing code. Alt+U chords are the primary ones. Visual Assist keys are
also bound in the Text Editor scope (`guidTextEditorScope`): Alt+O, Alt+G, Alt+M, Shift+Alt+S/O/G/F/R.

**Completion:** both pipelines are implemented. VS 2022 17.14's VC++ uses the *legacy* pipeline
(`Completion/UnrealLegacyCompletion.cs`, "🐧 Unreal" tab); the user's VS 2026 shows items from the *async*
`UnrealCompletionSource` in the main list. Both use `Completion/SymbolTooltip.cs` for the tooltip.

**Verifiability:** every feature logs to Output → "PenguinExtension" via `Services/PenguinLog.cs`; editor hooks call
`FeatureProbe.Mark`. **Penguin Self-Test (Alt+U, U)** prints a checklist (project, index counts, lookups, which hooks VS
called, key bindings). Command failures use `PenguinEditor.Fail` (logs + shows the pane). Never swallow exceptions silently.

**Typing assistance (v1.6, Unreal):** `Editor/PenguinKeyFilter.cs` is an IOleCommandTarget first in each C/C++ view's
command chain. It drives the 🐧 suggestion box (`UI/PenguinCompletionPopup.cs`, items from
`Completion/PenguinCompletionEngine.cs`: specifiers, Owner::/obj-> members, smart true/false/enum/nullptr/overrides,
`UnrealSnippets`, fuzzy `CacheService.SearchFuzzy` + `PenguinMatcher`) and the parameter info + definition peek box
(`UI/PenguinParameterInfoPopup.cs`, `Services/DefinitionLookup.cs`). Picking a symbol auto-adds its #include
(`Services/IncludeInserter.cs`). Create Implementation (Alt+U, I) uses `Services/CodeContext.DeclarationAtCaret`.
Indexer: `FlattenMacroArguments` handles nested parens in UCLASS/UFUNCTION(meta=(…)); `ExtractClassMembers` indexes
plain C++ members of reflected classes. Bump `ExtractorVersion` in UnrealIndexer when extraction changes.

**Backend:** the Rust Core backend is frozen — `PenguinOptionsPage.Backend` always returns Legacy.

| Key | Command |
|-----|---------|
| D   | Go To Unreal Definition |
| E   | Open Unreal Explorer |
| I   | Generate Implementation (.cpp) |
| G   | Generate Getter/Setter |
| S   | Symbol Inspector |
| K   | Keyboard Shortcuts |
| Shift+T | Penguin Color Theme window |
| Shift+P | Penguin Packs window |
| Shift+M | Penguin MCP window |
| Y / O / H / J / R / M | Find Symbol / Open File / Toggle Header-Source / Go To Implementation / Go To Related / List Methods |
| F / W | Find References / Definition Preview window |
| N / A / C / V / P / Q | Rename / Add Include / Create Declaration / Move Implementation / Encapsulate Field / Document Method |
| T / B / L / X / Z | Snippets & Surround With / Implement Virtual Methods / Add Missing Cases / Introduce Variable / Forward Declaration |

**Unity mode:** a solution next to `Assets/` + `ProjectSettings/ProjectVersion.txt` (any Unity version). No C++ index; the
`unity` pack adds C# message stubs, snippets and hover docs (MEF `ContentType("CSharp")`). Editor lookup: `UnityEditorLocator`.

**MCP:** `.mcp.json` (solution) / `.vs\mcp.json` / `.vscode\mcp.json` / `%USERPROFILE%\.mcp.json` + pack `mcp` suggestions.
Servers start only when the user clicks Connect in the MCP window. Connections close when the solution changes.

**Two C++ project modes:** Unreal (`.uproject` found) and plain C/C++ (any solution with `.vcxproj`/`.cpp`, e.g. SDL2 games). Plain mode indexes the solution with `Services/CppSymbolExtractor.cs` (general C/C++ declarations) plus library include folders from each `.vcxproj`'s AdditionalIncludeDirectories and the "Extra Include Directories" option. Core (Rust) backend is Unreal-only.

Signature help (declaration + docs) appears when typing `(` after an indexed Unreal function.

## Penguin Packs (docs/PACKS_PLAN.md)

Engine/library support is data in pack folders (`pack.json` + `specifiers.json`/`snippets.json`/headers), not C#.
Built-in packs live in `Packs/` (shipped in the VSIX: `unreal`, `sdl3`); user packs in `%LOCALAPPDATA%\PenguinExtension\Packs`;
solution packs in `<solution>\.penguin\packs` (later location wins by id). Per-solution check marks:
`<solution>\.vs\PenguinExtension\packs.json` (`{"enabled":{"sdl3":false}}`; missing = on if detected).
`PackService.Load` runs on the background thread after project detection; a check-mark change re-runs startup.
Symbols/files carry `pack_id` (schema v3): NULL = solution code, `unreal` = engine source, `external` = unclaimed library.
Add Unreal specifiers/snippets by editing `Packs/unreal/*.json`, never C# arrays. Self-Test lists every pack.
Packs window: Alt+U, Shift+P (check marks, import folder/zip/library). Pack `languages` decides where snippets show
(`cpp` → C++ popup, `csharp` → C# completion); `callbacks.json` = engine-called methods; `mcp` = suggested MCP servers.

## Free + donations

Everything is free and lives in this one VSIX (v1.13 merged the former Penguin Premium add-on back in; it is
discontinued). Navigation/refactoring IDs 0x08xx/0x09xx are in the main command set. Donations: Tools → Penguin →
Support Penguin (`DonateId`), the Shortcuts window link and the Self-Test footer all use `Services/PenguinLinks.DonateUrl`.

## Hard Rules

1. **Never block the UI thread.** All file I/O, SQLite queries, and regex scanning run on background threads. Use `JoinableTaskFactory.SwitchToMainThreadAsync()` only for VS UI access.

2. **Thread safety is non-negotiable.** `CacheService` uses `ReaderWriterLockSlim`. SQLite uses WAL mode. Don't change the DB mode.

3. **No C++ AST parsing.** Speed comes from regex. Use `RegexOptions.Compiled`. Avoid catastrophic backtracking.

4. **MEF content type is `C/C++`** for C/C++ features. Pack features for C# (Unity) use `[ContentType("CSharp")]`.

5. **Legacy .csproj format.** Files must be explicitly listed in `PenguinExtention.csproj` as `<Compile>` items.

6. **Namespace is `PenguinExtention`** (typo). Don't rename it.

## Build & Test

```bash
dotnet restore PenguinExtention.sln
msbuild PenguinExtention.sln /p:Configuration=Debug
# Debug: F5 launches VS Experimental Instance (devenv.exe /rootsuffix Exp)
```

## Startup Flow

```
AsyncPackage.InitializeAsync
  → SwitchToMainThreadAsync → RegisterCommands (GoTo, GenImpl, GenGetSet, 3 tool windows)
  → Background thread:
    → UnrealProjectDetector.DetectAsync
    → SQLiteCache init → CacheService.Initialize
    → StartupCacheLoader.LoadAsync
    → UnrealIndexer.IndexAsync (parallel regex scan)
    → IncrementalIndexer.Start (FileSystemWatcher)
```
