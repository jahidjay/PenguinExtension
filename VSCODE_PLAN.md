# VSCODE_PLAN.md — Penguin for VS Code and Google Antigravity (free edition)

**Goal (confirmed with the user 2026-10-14):** bring the free Visual Studio edition to VS Code and Antigravity
(a VS Code fork that installs the same `.vsix`; its store is Open VSX). Free edition only — Premium for VS Code
comes later. Deliver `.vsix` files (no store publishing yet) for **Windows, macOS and Linux**.

Base: `apps/vscode-ext` (TypeScript LSP client, v0.1) + Rust `penguin-lsp` (`crates/ue-lsp`), which already provide
completion with UE specifiers, hover, definition, document/workspace symbols, search/inheritance commands, local AI.

## Design (decided)

- **Penguin Dark / Penguin Light** = real VS Code color themes (`contributes.themes`), built on Dark+/Light+ via
  `include`, with TextMate rules + `semanticTokenColors` for the standard C++ token types (cpptools/clangd), using
  the colors from `SYNTAX_COLOR_PLAN.md`.
- **Unreal coloring** = editor decorations, not semantic tokens (VS Code uses only one semantic-token provider per
  document, and cpptools/clangd would win). Colors are contributed theme colors (`contributes.colors`, ids
  `penguin.ueMacro`, `penguin.specifier`, `penguin.metaKey`, `penguin.class`, `penguin.struct`, `penguin.enum`,
  `penguin.delegate`, `penguin.function`, `penguin.property`, `penguin.enumValue`) with dark/light defaults, so any
  theme works and users can override in `workbench.colorCustomizations`. Scanner = TS port of
  `Editor/UnrealClassifier.cs` `Scan` (macro/specifier/meta state machine, comment/string aware).
- **Names for coloring** come from a new LSP request `penguin/names` → `{revision, names: [[name, kind], …]}` (all
  indexed reflected names + live buffers). The client caches by revision and refetches after indexing.
- **Explorer** = sidebar tree view (activity bar "Penguin"), symbols grouped by kind, filter box → `penguin/symbols`.
  **Inspector** = webview view showing the symbol under the cursor (name lookup via `penguin/symbols`).
- **Signature help** = TS `SignatureHelpProvider` on `(`/`,` using `penguin/symbols` exact-name signature + docs.
- **Generators** = TS ports of Generate Implementation (header decl → `.cpp` skeleton, Public/Private aware) and
  Generate Getter/Setter (from UPROPERTY). Commands + editor context menu.
- **Platforms** = GitHub Actions job builds `penguin-lsp` per target and packages platform-specific `.vsix`
  (`vsce package --target win32-x64 linux-x64 darwin-x64 darwin-arm64`) as artifacts. Windows built locally too.

## Phases (tick when done; one-line note)

- [x] 1. `penguin/names` in Rust (ue-db query + ue-core Session + ue-lsp handler + protocol doc + tests)
- [x] 2. Themes (Penguin Dark/Light) + contributed colors + UE decorations (scanner port + tests)
- [x] 3. Explorer tree + Inspector webview
- [x] 4. Signature help + Generate Implementation / Getter-Setter
- [x] 5. Packaging: version 0.2.0, README, multi-platform workflow, local Windows `.vsix`, install in VS Code
- [x] 6. Antigravity: install the same `.vsix`; document Open VSX publishing for later

Done 2026-10-14: v0.2.0 built, Windows `.vsix` installed in VS Code and Antigravity IDE (awaiting the user's test).
CI `vscode-package` matrix builds win32-x64 / linux-x64 / darwin-x64 / darwin-arm64 artifacts. Open VSX / Marketplace
publishing still to do (needs the user's publisher accounts).

## Context-budget protocol
This file is the source of truth; re-read it rather than relying on chat history. Memory points here.
