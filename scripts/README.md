# Local build helpers

All helpers operate on the checkout. None installs an extension, changes an IDE profile, publishes a package, or starts/downloads an AI model.

- `bash setup_all.sh --check`: non-destructive prerequisite check.
- `bash setup_all.sh --restore`: explicit locked Rust dependency fetch after checking tools.
- `python scripts/check_bootstrap.py`: checks that repeated setup runs preserve manifests.
- `python -m unittest discover -s scripts -p 'test_*.py'`: package inspector, LSP framing, and language-neutral protocol fixture tests.
- `python scripts/smoke_lsp.py target/release/penguin-lsp.exe --temp-dir <scratch-directory>`: real executable initialize/index/status/details/live-overlay/shutdown smoke check. Uses only disposable source; no IDE installation or model service.
- `bash scripts/build-core.sh`: locked release builds of LSP and MCP.
- `python scripts/stage-vscode-server.py`: copy the host release LSP into the VS Code package's platform directory. Use `--target <Rust target>` only after building that target.
- `pwsh -File scripts/build-vsix.ps1`: build Rust, restore/build the legacy VSIX with full MSBuild, and verify the archive. `-MSBuildPath` selects a particular installed Visual Studio toolchain.
- `python scripts/verify_package.py visual-studio bin/Release/PenguinExtention.vsix`: inspect the Visual Studio archive without installing it. For VS Code, pass `vscode` and the generated package path.
- `python scripts/smoke_package.py visual-studio bin/Release/PenguinExtention.vsix --temp-dir <scratch-directory>`: inspect a locally built archive, copy only its native LSP into disposable storage, and run the protocol smoke test against that packaged binary. Use `vscode` for a VS Code package matching the host. This executes the bundled server but does not install or activate the IDE extension; only use packages you trust.

Package inspection confirms required entries, not IDE runtime compatibility. Only a native host build creates a verified binary for that OS/architecture. Do not rename a Windows executable to advertise Linux/macOS support. Keep platform packages separate.
