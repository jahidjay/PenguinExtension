"""Check packaging/configuration invariants without running application code."""
import json
from pathlib import Path
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
errors = []
ns = {"v": "http://schemas.microsoft.com/developer/vsx-schema/2011"}
manifest = ET.parse(ROOT / "source.extension.vsixmanifest")
identity = manifest.find("v:Metadata/v:Identity", ns)
if identity.get("Id") != "PenguinExtention.831518ca-a06f-433a-9cfb-15b7a6538550":
    errors.append("Existing VSIX identity changed")
if manifest.find("v:Dependencies/v:Dependency", ns).get("Version") != "[4.7.2,)":
    errors.append("VSIX framework requirement no longer matches net472")

for relative in ("apps/vscode-ext/package.json", "apps/desktop-tauri/package.json"):
    path = ROOT / relative
    if path.exists():
        package = json.loads(path.read_text(encoding="utf-8"))
        if any(key in package.get("scripts", {}) for key in ("postinstall", "preinstall")):
            errors.append(f"Unexpected automatic install hook in {relative}")
        if "publish" in package.get("scripts", {}):
            errors.append(f"Publishing must remain an explicit separate workflow: {relative}")

workflow = ROOT / ".github/workflows/verify.yml"
if workflow.exists():
    text = workflow.read_text(encoding="utf-8")
    if "secrets." in text or "contents: write" in text:
        errors.append("Verification workflow must not need publishing credentials")

setup = (ROOT / "setup_all.sh").read_text(encoding="utf-8")
for forbidden in ("rustup default", "rustup toolchain install", "ollama pull", "curl ", "touch crates/"):
    if forbidden in setup:
        errors.append(f"Unsafe bootstrap side effect: {forbidden}")

if errors:
    print(chr(10).join(errors), file=sys.stderr)
    raise SystemExit(1)
print("Source/package configuration invariants passed. This is not a security audit or runtime test.")
