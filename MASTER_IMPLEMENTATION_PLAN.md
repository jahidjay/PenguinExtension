# MASTER_IMPLEMENTATION_PLAN.md

## Current status — 2026-10-06 (supersedes the original scaffold plan)

PenguinCore has progressed from the proposed rewrite to **implemented and tested Rust libraries, LSP/MCP adapters, VS Code client, and Tauri desktop application**. The root Visual Studio bridge has **passed headless and package verification**. Do not rerun the historical scaffold below, overwrite existing manifests, or infer that the entire product has passed acceptance.

[docs/verification.md](docs/verification.md) is the authoritative measured-results ledger. Read its latest component/integration entries rather than treating the initial baseline as current. Test/build claims below refer to the recorded executions in that ledger; producing an installer does not establish installation or GUI acceptance.

| Workstream | Status and remaining gate |
| --- | --- |
| Parser, database and shared core | Implemented/tested: reflection parsing, pooled WAL persistence, sessions, bounded jobs/queries and writer-lease isolation. |
| Style and local AI | Implemented/tested. AI is opt-in and preview-only; mock HTTP coverage is not a live-model pass. |
| LSP and MCP | Implemented/tested; protocol adapters, live LSP overlays, disk-backed MCP queries and jobs. The headless ledger records 268 passing tests (including 11 LSP and 11 MCP subprocess tests), lint/format checks and Windows release binaries. |
| VS Code client | Implemented/tested: 27 unit tests and recorded typecheck/build/package checks passed. Windows-x64 VSIX and extracted-server smoke test verified; live VS Code/Antigravity acceptance remains open. |
| Tauri desktop | Implemented/tested: 24 frontend and 20 native tests passed after source-read hardening, with recorded build/lint checks. Windows executable and NSIS installer rebuilt with the fix, not installed; GUI acceptance remains open. |
| Visual Studio bridge | Implemented; 21 bridge/DTO assertions, VS2022/VS18 MSBuild builds, 71-entry VSIX inspection and extracted-server smoke check passed. Interactive IDE behavior remains unverified; supported installation range stays `[17.0, 18.0)`. |
| Platform/release acceptance | Unverified beyond the exact host checks in the ledger. Linux/macOS/ARM native execution, interactive IDE/GUI behavior, live-model use and broader packaging acceptance require separate evidence. |

### Remaining work, not a new scaffolding phase

1. Complete isolated Visual Studio runtime acceptance before changing the default backend or widening the supported installation range. Legacy remains the default; explicitly selected Core failures do not silently fall back.
2. Execute the [isolated manual acceptance checklist](docs/manual-acceptance.md) for the intended IDE/desktop targets when authorized. Keep headless tests, GUI acceptance, installation tests, and live-model checks separate.
3. Validate platform/architecture targets and release packages individually; checked-in CI configuration or cross-platform intent is not a recorded successful run.
4. Preserve known product limits: reflected declarations rather than general C++ semantic analysis, no inferred member completion/Rider parity, preview-only AI, explicit disk refresh for desktop/MCP, and versioned client contracts.

For the implemented architecture use the [core API](crates/ue-core/README.md), [LSP](crates/ue-lsp/README.md), [MCP](crates/ue-mcp/README.md), [VS Code](apps/vscode-ext/README.md), [desktop](apps/desktop-tauri/README.md), and [protocol v1](docs/penguin-protocol.md) documentation. In particular, `ue-core` is now a shared crate absent from the original scaffold; desktop native checks use a separate workspace.

---

## Historical proposal — retained for design intent only

The text and script below are the original migration proposal, not operative agent instructions. Its rule-override language is superseded: it does not authorize ignoring `CLAUDE.md`. The root C# VSIX still uses its legacy project/namespace and thread-safety constraints, while the implemented Rust parser uses tree-sitter. The original description of the C# extension as "single-threaded" was inaccurate; it already used asynchronous/background indexing. References to macOS/Linux/Windows or VS2022/2026 below are intended scope, not measured compatibility.

The historical execution protocol records the original phase-gate plan. It is not a current instruction to restart setup or completed work. The scaffold example would overwrite the root manifest and is retained **for reference only; do not execute it**. Use the repository's maintained setup documentation instead.


## Historical agent directive (superseded; not operative)
**Read this first:** This project is executing a greenfield rewrite. You must IGNORE the legacy `CLAUDE.md` rules regarding "No C++ AST parsing", "compiled regex only", and "Keep legacy .csproj". We are officially migrating from a single-threaded C# VSIX to a multi-crate Rust workspace with TypeScript/C# thin clients. If the Rust toolchain is missing, use the provided setup script below to install it.

## Historical execution protocol: phase-by-phase greenlight
**Strict Execution Rule:** You must execute this plan exactly **ONE PHASE AT A TIME**.
1. Complete the code and unit tests for the current phase.
2. **HALT EXECUTION.** Do not begin the next phase.
3. Prompt the user with: *"Phase [X] complete. Waiting for your greenlight to begin Phase [Y]."*
4. Only proceed when the user explicitly gives the greenlight.

---

## 1. Original Architecture & Intended Scope
PenguinCore is a cross-platform (macOS, Linux, Windows) LSP daemon, MCP server, and Tauri desktop app.
* **Core Engine:** Rust (edition 2021)
* **Parsing:** `tree-sitter-cpp` (replacing regex)
* **Storage:** `rusqlite` + `r2d2` pool in WAL mode
* **Generative AI:** Local inference via Ollama (`qwen2.5-coder:3b` default)
* **Clients:** Tauri v2 (Desktop), TypeScript (Antigravity/VS Code), C# VSIX (VS 2022/2026)

---

## 2. Original Setup Scaffold (`setup_all.sh`) — do not execute
Agent: Write this single script to `setup_all.sh` and execute it to prepare the environment, install Rust, and scaffold the workspace. **Halt and wait for greenlight after setup completes.**

```bash
#!/usr/bin/env bash
set -euo pipefail

echo "=== 1. Installing Toolchains ==="
if ! command -v cargo >/dev/null; then
    curl --proto '=https' --tlsv1.2 -sSf [https://sh.rustup.rs](https://sh.rustup.rs) | sh -s -- -y
    source "$HOME/.cargo/env"
fi
if ! command -v npm >/dev/null; then
    echo "Please install Node.js/npm manually for your OS."
fi

echo "=== 2. Scaffolding Workspace ==="
mkdir -p crates/ue-parser/src crates/ue-db/src crates/ue-style/src crates/ue-ai/src \
         crates/ue-lsp/src crates/ue-mcp/src apps/desktop-tauri/src apps/vscode-ext/src apps/vs-client

touch crates/ue-parser/src/lib.rs crates/ue-db/src/lib.rs crates/ue-style/src/lib.rs \
      crates/ue-ai/src/lib.rs crates/ue-lsp/src/lib.rs crates/ue-mcp/src/lib.rs

echo "=== 3. Root Cargo.toml ==="
cat << 'EOF' > Cargo.toml
[workspace]
resolver = "2"
members = ["crates/*"]

[workspace.dependencies]
tree-sitter = "0.22"
tree-sitter-cpp = "0.22"
rusqlite = { version = "0.31", features = ["bundled"] }
r2d2 = "0.8"
r2d2_sqlite = "0.24"
tower-lsp = "0.20"
tokio = { version = "1.38", features = ["full"] }
reqwest = { version = "0.12", default-features = false, features = ["json", "rustls-tls"] }
serde = { version = "1.0", features = ["derive"] }
serde_json = "1.0"
EOF

echo "Setup complete. Workspace ready."
```
