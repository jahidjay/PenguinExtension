# Penguin Desktop

A local Tauri 2 workspace browser backed in-process by `ue-core`, `ue-style`, and `ue-ai`. Its native crate has a separate Cargo workspace so headless LSP/MCP builds do not require desktop webview libraries.

## Build and verify

Run in this directory with Rust, a C/C++ compiler, Node.js 22.12+, and the platform's Tauri prerequisites:

```sh
npm ci
npm run typecheck
npm test
npm run build
cargo test --locked --manifest-path src-tauri/Cargo.toml
cargo clippy --locked --manifest-path src-tauri/Cargo.toml --all-targets -- -D warnings
npm run format:check
npm run desktop:portable -- -- --locked
```

`npm run desktop:dev` starts the development app and Vite server. `npm run desktop:build -- -- --locked` builds a release installer using the configured NSIS target on Windows. `desktop:portable` builds without an installer. Both release scripts explicitly enable `custom-protocol` so the built frontend is embedded rather than loading the development URL.

Windows output:

- `src-tauri/target/release/penguin-desktop.exe`
- `src-tauri/target/release/bundle/nsis/Penguin Desktop_0.1.0_x64-setup.exe`

The installer does not install WebView2; a compatible WebView2 runtime must already be available. Build output is unsigned. No build command installs the app or a model. The checked-in bundle target is Windows NSIS; Linux/macOS frontend/native CI configuration is not a claim of released native installers for those platforms.

## Workspace and data boundaries

Use the native directory chooser to open roots, then status/reindex and symbol search to browse declarations, optional metadata, and inheritance. Style results are disk-backed. **Refresh/reindex explicitly after files change**: this app does not watch IDE buffers or share another editor's unsaved changes. Stale session/symbol references must be refreshed instead of reused.

AI is disabled by default. Settings configure a loopback Ollama HTTP origin (default `http://127.0.0.1:11434`) and an already-installed model (default `qwen2.5-coder:3b`). Enabling AI does not start Ollama, download a model, or substitute a missing one. Explain/generate actions return cancellable preview text, not source edits. Source and generated text are rendered as text, not executable HTML. The native AI boundary limits source to 32 KiB, instructions to 2048 UTF-8 bytes, and responses to 256 KiB.

Settings are validated and atomically persisted in the Tauri app configuration directory. IPC commands are narrow workspace/query/job operations, not a general filesystem or shell bridge. Disk indexing may update the root-scoped SQLite cache but does not rewrite project source.

## Validation status

See [the verification ledger](../../docs/verification.md) for measured test/build results and [isolated acceptance](../../docs/manual-acceptance.md) for manual checks. Native command tests, frontend unit tests, and installer production do not establish an interactive GUI acceptance pass. No normal IDE profile is touched.
