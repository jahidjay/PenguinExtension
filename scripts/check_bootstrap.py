"""Prove that repeated prerequisite checks do not rewrite project inputs."""
from pathlib import Path
import hashlib
import os
import shutil
import subprocess

ROOT = Path(__file__).resolve().parents[1]
paths = [ROOT / "Cargo.toml", ROOT / "setup_all.sh", *ROOT.glob("crates/*/Cargo.toml")]

def fingerprints():
    return {str(path): hashlib.sha256(path.read_bytes()).hexdigest() for path in paths}

# Windows' system bash.exe launches WSL, not the Git Bash used by this repo.
shell = shutil.which("bash")
if os.name == "nt":
    git = shutil.which("git")
    candidates = [Path(git).resolve().parent.parent / "bin/bash.exe"] if git else []
    candidates += [Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe"]
    shell = next((str(path) for path in candidates if path.is_file()), None)
if not shell:
    raise SystemExit("Install Git Bash (Windows) or bash (Unix) before checking bootstrap.")
before = fingerprints()
for _ in range(2):
    subprocess.run([shell, "setup_all.sh", "--check"], check=True, cwd=ROOT)
assert fingerprints() == before, "Prerequisite checking changed a project input"
print("Repeated prerequisite checks preserved all manifests and setup source.")
