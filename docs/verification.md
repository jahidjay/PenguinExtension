# Verification ledger

## Baseline — 2026-10-06

Before remaining-component implementation:

- `cargo test --workspace`: **152 passed**, including four actual LSP subprocess tests. No failures.
- VS2022 Community full MSBuild, solution Debug restore/build: **passed**, producing `bin/Debug/PenguinExtention.vsix`. Existing warnings include CS1998 and VSTHRD threading/analyzer warnings; this is not a warning-free baseline.
- Repaired setup prerequisite checker, twice: **passed**, manifests unchanged.
- Package-inspection helper tests: **4 passed**.

## Completed component checks — 2026-10-06

- `ue-parser` + `ue-db`: **61 unit/integration tests and two documentation tests passed**. Targeted Clippy passed. Initial formatting check found formatting-only differences in three existing test/assertion blocks and specifier assertions; `cargo fmt -p ue-parser -p ue-db` corrected them and the recheck passed.
- `ue-core`: **22 unit tests and 17 integration tests passed**, including an OS-process lease test. Targeted Clippy with warnings denied and formatting check passed.
- `ue-style`: **20 integration tests and one documentation test passed**. Targeted Clippy with warnings denied and formatting check passed. Tests cover disabled rules, lexical false positives, malformed/truncated input, Unicode/CRLF byte ranges, conservative declaration matching, and deterministic ordering.
- `ue-ai`: **28 integration tests passed** using ephemeral loopback mock HTTP servers only. Targeted Clippy with warnings denied and formatting check passed. Coverage includes loopback validation, proxy/redirect rejection, request/response bounds, unavailable models/services, deadlines, and dropped-future cancellation. No live model was contacted by these tests.
- Local helper suite: **16 tests passed**, covering package inspection, isolated packaged-server extraction, LSP byte framing, and protocol fixtures. This count supersedes the four initial package-helper tests above.
- Source/package configuration invariant checker: **passed** at this stage; this is a static consistency check, not a security audit or runtime test.

## Integrated headless and VS Code checks — 2026-10-06

- `cargo test --locked --workspace`: **265 unit/integration tests + 3 documentation tests = 268 passed** (not counting a lease-test subprocess's nested repeat). This includes **11 LSP** and **11 MCP** real-process tests. The added MCP cancellation regression verifies that a cancellation racing job submission leaves no queued/running orphan, with unit coverage for both registration/drop orderings and successful response delivery.
- `cargo clippy --locked --workspace --all-targets -- -D warnings`: **passed**.
- `cargo fmt --all -- --check`: **passed**.
- Documentation for parser/database/core/style/AI, with warnings denied: **passed**.
- Locked release builds: `target/release/penguin-lsp.exe` and `target/release/penguin-mcp.exe` **produced**.
- VS Code client: reproducible `npm ci`, strict typecheck, **27 unit tests**, and production compilation **passed**. During integration the client endpoint validation was aligned with the backend's HTTP-origin-only policy and its default model with the documented `qwen2.5-coder:3b`; AI remains disabled by default.
- npm initially reported six high-severity **development-tool** dependency advisories through vsce 3.x. Updated packaging tool to pinned `@vscode/vsce@4.0.0`; subsequent audit: **zero reported vulnerabilities**. This is an audit snapshot, not a blanket security guarantee.
- Local VS Code package: **`apps/vscode-ext/dist/penguin-vscode.vsix`**, Windows x64 server bundled; **329 entries, 4,644,661 bytes**, SHA-256 `18e9a4b92c2857f6311721eaa0ae4e7487465df218aa51e2ff715637e68c2ee5`. The packaged `extension/bin/win32-x64/penguin-lsp.exe` is byte-identical to `target/release/penguin-lsp.exe` (12,413,440 bytes, SHA-256 `ca65f61b0b6fb95677300ab3d8e654eb385cf28a010c6f06c67c650094e715f1`). Archive inspection and smoke test on the server extracted from that package **passed**. Packaging reports a non-fatal recommendation to bundle its JavaScript dependencies (174 JS files); no runtime failure was observed in the server smoke test.
- Release and packaged LSP smoke checks cover initialize/protocol version, indexing/status, details/inheritance, live custom queries, unsaved deletions, stale IDs, close restoration, and shutdown/exit with stdin open. Fixture source hashes remained unchanged.

- Repeated bootstrap prerequisite checks preserved setup and all Rust manifests. Source/package static invariants and `git diff --check` passed after integration.

## Desktop checks — 2026-10-06

- Reproducible `npm ci`, strict typecheck, **24 frontend tests**, and Vite production build: **passed**.
- After source-read hardening, an independent `cargo test --offline --locked --manifest-path apps/desktop-tauri/src-tauri/Cargo.toml` rerun: **20 native tests passed**, including nine source-file boundary tests; binary and documentation targets also passed with zero tests. This supersedes the earlier 13-test result. The earlier `native:check` passed.
- Source reads now use no-follow leaf opening, live opened-handle paths before/after reading, regular-file/reparse checks, and retained workspace-root identities. Regression tests cover ancestor junction redirection restored before validation, equal-size/equal-mtime file replacement, root replacement, directory/reparse substitutions, growth and exact-size limits. These tests ran on Windows x86_64 MSVC; Linux/macOS implementations and Unix-only coverage were not compiled or run locally.
- This is defense in depth, not an atomic filesystem sandbox or content snapshot: hard links, concurrent namespace changes between observations, and in-place writes remain limitations. Windows identity checks use legacy 64-bit file IDs, not ReFS 128-bit IDs. Linux requires usable `/proc/self/fd`; unavailable handle-path queries fail closed.
- **Rebuilt after the hardening fix** with `CARGO_NET_OFFLINE=true` and `npm --prefix apps/desktop-tauri run desktop:build -- -- --locked`: frontend typecheck/Vite build, native release build with `custom-protocol`, and NSIS packaging **passed**. The initial invocation with only `-- --locked` failed Tauri argument parsing before compilation; the corrected command forwards `--locked` to Cargo, and the README/CI invocation was corrected too.
- Windows x64 app: **`apps/desktop-tauri/src-tauri/target/release/penguin-desktop.exe`**, **18,465,792 bytes**, SHA-256 `eb9f741b65d4fcd5dd4aabfe45da4457615ead5231712b3c93f197bdf100dd8d`. The current file is the NSIS-bundle-patched release app, not the earlier no-bundle output. PE signature/machine check passed (`0x8664`, x64); compilation does not establish GUI behavior.
- Windows installer for the x64 app: **`apps/desktop-tauri/src-tauri/target/release/bundle/nsis/Penguin Desktop_0.1.0_x64-setup.exe`**, **4,160,319 bytes**, SHA-256 `7f3489b6d3cf7ec766d0f5bfc761c4d2a51ca54b51728ebecaad03f86b38db4e`, **produced but not installed**. PE signature check passed (the NSIS launcher itself is x86). These outputs supersede the pre-fix app and 4,150,406-byte installer. WebView2 installation is deliberately skipped by this installer.
- Independent post-hardening native Clippy (`--offline --locked --all-targets -- -D warnings`) and native formatting: **passed**. Desktop frontend rerun: **24 tests passed**; Prettier check **passed**.
- Repeated final headless suite: **268 tests passed** (excluding the nested lease-test repeat), Clippy/formatting passed; helper suite still **16 passed**.

## Visual Studio checks — 2026-10-06

- The .NET Framework 4.7.2 bridge/DTO harness passed **21 assertions**, including the real-server path against `target/release/penguin-lsp.exe`.
- `scripts/build-vsix.ps1` completed with both the installed Visual Studio 2022 Community and Visual Studio 18 Community MSBuild toolchains. Both builds produced a verified **71-entry** VSIX containing `Core/penguin-lsp.exe` (ZIP member names are case-sensitive).
- Visual Studio VSIX: **`bin/Release/PenguinExtention.vsix`**, 7,853,379 bytes, SHA-256 `c870d032d6d66a57dc6b4ce044574fca3f7452c76c65a26d30492f5624a5bbdb`. The packaged `Core/penguin-lsp.exe` is byte-identical to `target/release/penguin-lsp.exe` (12,413,440 bytes, SHA-256 `ca65f61b0b6fb95677300ab3d8e654eb385cf28a010c6f06c67c650094e715f1`). Rust release builds are not byte-reproducible, so earlier recorded package hashes from preceding builds differ; package freshness is established by direct byte comparison of the embedded server against the current release binary, not by hash history.
- The packaged-server smoke test passed without installing the extension or changing an IDE profile.
- Builds still report existing VSTHRD003/100/101/103/110 analyzer warnings; this is not a warning-free result.
- The manifest installation and prerequisite range remains **`[17.0, 18.0)`**. Compilation with the Visual Studio 18 toolchain does not establish runtime compatibility, and no isolated Visual Studio 18 runtime acceptance was performed.
- No interactive Visual Studio acceptance was performed. Headless/client-unit and package results do not establish editor, command, MEF, or tool-window behavior inside the IDE.

## Validation boundaries

A compiler/build result does not establish IDE/UI compatibility. Native packages, headless tests, interactive checks, live-model checks, and platform CI runs must be recorded separately. No normal IDE profiles are modified by these checks. The default Ollama model is not installed; deterministic tests use a mock local HTTP server.

Final package recheck: both VSIX archives still contain byte-identical copies of the measured release LSP; extracted-server smoke tests passed again with fixture source unchanged. Source/package invariants, the 16-test Python helper suite, and `git diff --check` passed. Git emitted existing LF-to-CRLF notices, not whitespace errors.

Interactive Visual Studio/VS Code/Antigravity acceptance, desktop GUI and installer acceptance, Linux/macOS/ARM execution, and live-model acceptance remain unperformed. The cross-platform CI workflow has been configured and its Tauri argument forwarding corrected, but no CI run is claimed. No commits, pushes, external publication, source uploads, IDE-profile installation, or model downloads were performed.

The sections above record implementation, automated verification and local packages. Interactive acceptance and unrun platform checks remain open; the baseline is retained only as historical evidence.
