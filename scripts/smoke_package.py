"""Run the LSP smoke check on a server copied out of a local, validated VSIX."""
import argparse
from pathlib import Path
import platform
import tempfile
import zipfile

from smoke_lsp import smoke
from verify_package import inspect


def run(package, kind, temporary_parent=None):
    inspect(package, kind)
    system = {"Windows": "win32", "Linux": "linux", "Darwin": "darwin"}.get(platform.system())
    arch = {"AMD64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(platform.machine())
    if not system or not arch:
        raise ValueError("Unsupported smoke-test host")
    if kind == "visual-studio" and (system, arch) != ("win32", "x64"):
        raise ValueError("The Visual Studio package requires a Windows x64 test host")
    executable = "penguin-lsp.exe" if system == "win32" else "penguin-lsp"
    entry = "Core/" + executable if kind == "visual-studio" else f"extension/bin/{system}-{arch}/{executable}"
    # Do not extract the archive tree or invoke its JavaScript/managed extension.
    # Only the verified native language server is run on disposable fixture data.
    with tempfile.TemporaryDirectory(prefix="penguin-package-", dir=temporary_parent) as folder:
        binary = Path(folder) / executable
        with zipfile.ZipFile(package) as archive:
            names = {name.replace(chr(92), "/").lower(): name for name in archive.namelist()}
            selected = names.get(entry.lower())
            if selected is None:
                raise ValueError(f"No native server for this host: {entry}")
            binary.write_bytes(archive.read(selected))
        binary.chmod(0o700)
        smoke(binary, temporary_parent)
    print(f"PASS: packaged server smoke check; no IDE/profile installation: {package}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("kind", choices=["visual-studio", "vscode"])
    parser.add_argument("package", type=Path)
    parser.add_argument("--temp-dir", type=Path, help="Existing scratch directory for temporary extraction and fixtures")
    args = parser.parse_args()
    run(args.package.resolve(), args.kind, args.temp_dir)
