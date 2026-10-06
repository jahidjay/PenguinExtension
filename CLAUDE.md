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
  PenguinOptionsPage.cs      — Tools → Options page
UI/
  UnrealExplorerWindow.cs          — Symbol Explorer tool window
  SymbolInspectorWindow.cs         — Live symbol documentation panel
  SymbolInspectorControl.xaml/.cs  — WPF control for inspector
  ShortcutsReferenceWindow.cs      — Keyboard shortcuts reference
  ShortcutsReferenceControl.xaml/.cs — WPF control for shortcuts
```

## Keyboard Shortcuts (all two-chord: Ctrl+Shift+U, then…)

| Key | Command |
|-----|---------|
| D   | Go To Unreal Definition |
| E   | Open Unreal Explorer |
| I   | Generate Implementation (.cpp) |
| G   | Generate Getter/Setter |
| S   | Symbol Inspector |
| K   | Keyboard Shortcuts |

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
