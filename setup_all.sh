#!/usr/bin/env bash
# Non-destructive prerequisite check. Dependency restoration is explicit.
set -euo pipefail
export PATH="$HOME/.cargo/bin:$PATH"
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
mode="${1:---check}"
case "$mode" in
  --check|--restore) ;;
  --help) printf '%s
' 'Usage: bash setup_all.sh [--check|--restore]' 'Checks installed tools; --restore also fetches Rust dependencies.' 'Never installs toolchains, downloads AI models, or rewrites source.'; exit 0 ;;
  *) printf 'Unknown option: %s
' "$mode" >&2; exit 2 ;;
esac
if [ "$#" -gt 1 ]; then printf '%s
' 'Only one option is accepted.' >&2; exit 2; fi
missing=0
for tool in cargo rustc node npm; do
  if command -v "$tool" >/dev/null 2>&1; then "$tool" --version;
  else printf 'Missing prerequisite: %s (install it explicitly, then rerun).
' "$tool" >&2; missing=1; fi
done
if [ ! -f "$root/Cargo.toml" ]; then printf '%s
' 'Workspace manifest missing; restore the checkout instead of regenerating it.' >&2; exit 1; fi
if [ "$missing" -ne 0 ]; then exit 1; fi
printf '%s
' 'Native builds also require a C/C++ compiler; VSIX needs Visual Studio/MSBuild; Tauri needs platform webview prerequisites.'
if [ "$mode" = '--restore' ]; then cargo fetch --locked --manifest-path "$root/Cargo.toml"; fi
printf '%s
' 'Prerequisite check complete. Existing files and global toolchains were not changed.'
