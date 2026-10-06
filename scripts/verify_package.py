"""Inspect a local VSIX without installing or executing it."""
import argparse
from pathlib import Path
import zipfile


def inspect(path, kind):
    with zipfile.ZipFile(path) as archive:
        names = {name.replace("\\", "/").lower(): name for name in archive.namelist()}
        if len(names) != len(archive.namelist()):
            raise ValueError("Archive contains duplicate or case-colliding paths")
        unsafe = [name for name in names if name.startswith("/") or ".." in name.split("/") or ":" in name]
        if unsafe:
            raise ValueError(f"Unsafe archive paths: {unsafe}")
        if kind == "visual-studio":
            required = ["extension.vsixmanifest", "penguinextention.dll", "core/penguin-lsp.exe"]
            for entry in required:
                if entry not in names:
                    raise ValueError(f"Missing Visual Studio package entry: {entry}")
            binaries = ["core/penguin-lsp.exe"]
        else:
            import json
            manifest_path = names.get("extension/package.json")
            if not manifest_path:
                raise ValueError("Missing VS Code extension/package.json")
            manifest = json.loads(archive.read(manifest_path))
            main = "extension/" + manifest["main"].removeprefix("./").lower()
            if main not in names:
                raise ValueError(f"Missing extension entry point: {main}")
            binaries = [n for n in names if "/bin/" in n and n.endswith(("/penguin-lsp.exe", "/penguin-lsp"))]
        if not binaries:
            raise ValueError("No bundled language server; end users must not need Cargo")
        for binary in binaries:
            info = archive.getinfo(names[binary])
            if info.file_size < 4096:
                raise ValueError(f"Bundled server looks truncated: {binary}")
            with archive.open(names[binary]) as stream:
                magic = stream.read(4)
            if binary.endswith(".exe"):
                valid_magic = magic.startswith(b"MZ")
            elif "/linux-" in binary:
                valid_magic = magic == bytes([0x7f, 0x45, 0x4c, 0x46])
            elif "/darwin-" in binary:
                valid_magic = magic in [bytes.fromhex(value) for value in ("feedfacf", "cffaedfe", "cafebabe", "bebafeca")]
            else:
                valid_magic = False
            if not valid_magic:
                raise ValueError(f"Bundled server has no matching native executable header: {binary}")
        print(f"Verified {kind} package {path}: {len(names)} entries; servers: {', '.join(binaries)}")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("kind", choices=["visual-studio", "vscode"])
    parser.add_argument("path", type=Path)
    args = parser.parse_args()
    inspect(args.path, args.kind)
