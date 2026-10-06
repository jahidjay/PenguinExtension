"""Stage a locally built language server for a platform-specific VS Code package."""
import argparse
from pathlib import Path
import platform
import shutil

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--target", help="Rust cross-compilation target; omit for host build")
args = parser.parse_args()
if args.target:
    targets = {
        "x86_64-pc-windows-msvc": ("win32", "x64"),
        "aarch64-pc-windows-msvc": ("win32", "arm64"),
        "x86_64-unknown-linux-gnu": ("linux", "x64"),
        "aarch64-unknown-linux-gnu": ("linux", "arm64"),
        "x86_64-apple-darwin": ("darwin", "x64"),
        "aarch64-apple-darwin": ("darwin", "arm64"),
    }
    if args.target not in targets:
        parser.error("Unsupported package target; do not guess a binary platform")
    system, arch = targets[args.target]
    build = ROOT / "target" / args.target / "release"
else:
    system = {"Windows": "win32", "Linux": "linux", "Darwin": "darwin"}.get(platform.system())
    arch = {"AMD64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(platform.machine())
    if not system or not arch:
        parser.error("Unsupported host architecture")
    build = ROOT / "target/release"
name = "penguin-lsp.exe" if system == "win32" else "penguin-lsp"
source = build / name
if not source.is_file():
    parser.error(f"Build the release server first: {source}")
destination = ROOT / "apps/vscode-ext/bin" / f"{system}-{arch}" / name
destination.parent.mkdir(parents=True, exist_ok=True)
if destination.exists() and destination.read_bytes() != source.read_bytes():
    print(f"Replacing the previous staged build: {destination}")
shutil.copy2(source, destination)
print(f"Staged {source} -> {destination}")
