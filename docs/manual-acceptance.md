# Isolated acceptance checklist

Use a disposable fixture workspace and experimental/isolated IDE profile. Do not install development builds in the normal profile. Record which items were actually performed; this list is not a passing test report.

## Fixture

Create a small project containing a reflected class, a property with contradictory access flags, a function, a struct, an enum, and a second file referencing the class. Include a path containing spaces and non-ASCII characters. Engine indexing may be tested with a separately configured explicit source root.

## Language features and lifecycle

- Start Core, confirm its version/status, and observe the initial index complete.
- Confirm macro completion, local/global reflected names, hover, outline, and definition navigation.
- Edit a header without saving: the second file sees the new symbol, and deleted declarations disappear. Close without saving: disk declarations return.
- Create/change/delete a header and confirm watcher updates. Reindex twice; symbols do not duplicate.
- Check UTF-16 positions using an emoji comment before a declaration/caret.
- Open two files with the same reflected name; navigation preserves ambiguity instead of guessing.
- Trigger and remove a style warning; no stale diagnostics survive close/reopen or backend restart.
- Change workspace/roots/configuration and confirm clean restart, no orphan child, and no results from the previous session.
- Kill the server child deliberately in the test profile; confirm actionable error/restart rather than silent legacy fallback.
- Close the editor normally; confirm shutdown/exit and writer lease release.

## Visual Studio-specific

- Select Legacy: existing indexer and features work. Select Core and restart/reopen as instructed: no legacy cache hydration/indexing/watchers run.
- Check Ctrl+Shift+U then D/E/I/G/S/K still map to existing commands.
- Verify Explorer/Inspector show actual Core results and unknown metadata stays absent.
- Generate implementation/getters with unsaved destination changes; edits respect the editor buffer and undo.
- Test feature enable options, status/error reporting, cancellation, and solution close/reopen.
- VS2022 and VS2026 results are separate. Build success with a newer MSBuild does not permit widening the installation range by itself.

## AI

- Disabled by default; explicit commands explain why it is unavailable.
- With an unavailable service/model, show an actionable failure without starting Ollama or downloading anything.
- If deliberately configured with an installed local model, request an explanation and a proposed implementation; keep output separate from source.
- Cancel a running request, switch workspace, and verify no late preview appears in the new session.
- Treat a returned HTML/script-looking string as text, never execute it.

## Desktop/MCP

- Desktop root chooser, status/reindex, search/details/inheritance, style, settings restart persistence, and AI preview/cancellation work.
- Desktop disk refresh is explicit; it does not claim to see IDE unsaved buffers.
- MCP client negotiates initialization, lists tools, and calls source-read-only tools against configured roots. Outside paths fail. Logs do not corrupt stdout.
- Test busy cache ownership with two same-adapter sessions; the second reports busy without deleting files or launching another prune worker.

Record app/IDE version, OS/architecture, package path, performed steps, and failures in the verification ledger. Antigravity, Linux, and macOS require their own host runs.
