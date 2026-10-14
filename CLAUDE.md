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
Editor/                      — Classifier (UE coloring), reference-highlight tagger, signature help
  UnrealClassifier.cs              — Unreal syntax coloring: types/structs/enums/delegates/functions/properties + specifiers/meta keys
Completion/
  UnrealCompletionSource.cs         — IAsyncCompletionSource: symbols + macro specifiers
  UnrealCompletionSourceProvider.cs — MEF factory
  UnrealMacroSpecifiers.cs          — Static specifier data (UPROPERTY/UFUNCTION/UCLASS/etc.)
Database/SQLiteCache.cs      — SQLite persistence layer (WAL, concurrent R/W)
Models/                      — UnrealSymbol, UnrealClassInfo, UnrealSymbolKind enum, IndexedFile
QuickInfo/                   — IAsyncQuickInfoSource hover tooltips
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
  PremiumHooks.cs            — Delegates the Premium add-on fills in (Explorer "Go to Implementation"/"Find References")
  ThemeModel.cs / ThemeService.cs — Penguin Color Theme: entries + defaults, theme.json, runtime apply (SYNTAX_COLOR_PLAN.md)
UI/
  UnrealExplorerWindow.cs          — Symbol Explorer tool window
  SymbolInspectorWindow.cs         — Live symbol documentation panel
  SymbolInspectorControl.xaml/.cs  — WPF control for inspector
  ShortcutsReferenceWindow.cs      — Keyboard shortcuts reference
  ShortcutsReferenceControl.xaml/.cs — WPF control for shortcuts
  ColorThemeWindow.cs              — Penguin Color Theme window (code-built WPF)
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
| Y / O / H / J / R / M | Find Symbol / Open File / Toggle Header-Source / Go To Implementation / Go To Related / List Methods — **Premium** |
| F / W | Find References / Definition Preview window — **Premium** |
| N / A / C / V / P / Q | Rename / Add Include / Create Declaration / Move Implementation / Encapsulate Field / Document Method — **Premium** |
| T / B / L / X / Z | Snippets & Surround With / Implement Virtual Methods / Add Missing Cases / Introduce Variable / Forward Declaration — **Premium** |

**Two project modes:** Unreal (`.uproject` found) and plain C/C++ (any solution with `.vcxproj`/`.cpp`, e.g. SDL2 games). Plain mode indexes the solution with `Services/CppSymbolExtractor.cs` (general C/C++ declarations) plus library include folders from each `.vcxproj`'s AdditionalIncludeDirectories and the "Extra Include Directories" option. Core (Rust) backend is Unreal-only.

Signature help (declaration + docs) appears when typing `(` after an indexed Unreal function.

## Free vs Premium

This repo is the **free** edition (public). **Penguin Premium** is a separate VSIX in a separate private repo
(`../PenguinPremium`, `jahidjay/PenguinPremium`) that depends on this extension and references this assembly via
`[InternalsVisibleTo("PenguinPremium")]`. Premium owns: Definition Preview window, Find References window, and every
Visual Assist-style navigation/refactoring command (IDs 0x08xx/0x09xx under its own command set GUID, parented to
this extension's Tools → Penguin menu groups). Never add premium code here; free features that offer a premium
action go through `Services/PremiumHooks.cs`. Internal APIs Premium uses are a contract — keep them stable.

## Hard Rules

1. **Never block the UI thread.** All file I/O, SQLite queries, and regex scanning run on background threads. Use `JoinableTaskFactory.SwitchToMainThreadAsync()` only for VS UI access.

2. **Thread safety is non-negotiable.** `CacheService` uses `ReaderWriterLockSlim`. SQLite uses WAL mode. Don't change the DB mode.

3. **No C++ AST parsing.** Speed comes from regex. Use `RegexOptions.Compiled`. Avoid catastrophic backtracking.

4. **MEF content type is `C/C++`.** All completion and QuickInfo providers must use `[ContentType("C/C++")]`.

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
