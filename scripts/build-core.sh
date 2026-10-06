#!/usr/bin/env bash
# Build only local headless binaries; no install or publish side effects.
set -euo pipefail
export PATH="$HOME/.cargo/bin:$PATH"
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cargo build --locked --release --manifest-path "$root/Cargo.toml" -p ue-lsp -p ue-mcp
printf '%s
' "Built language server and MCP server under $root/target/release."
