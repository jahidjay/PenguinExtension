import importlib.util
from pathlib import Path
import tempfile
import unittest
import zipfile

spec = importlib.util.spec_from_file_location("verify_package", Path(__file__).with_name("verify_package.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PackageChecks(unittest.TestCase):
    def check_zip(self, files, kind, valid):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "test.vsix"
            with zipfile.ZipFile(path, "w") as archive:
                for name, content in files.items():
                    archive.writestr(name, content)
            if valid:
                module.inspect(path, kind)
            else:
                with self.assertRaises(ValueError):
                    module.inspect(path, kind)

    def test_missing_server_is_rejected(self):
        self.check_zip({"extension.vsixmanifest": "x", "PenguinExtention.dll": "x"}, "visual-studio", False)

    def test_visual_studio_requires_actual_binary(self):
        files = {"extension.vsixmanifest": "x", "PenguinExtention.dll": "x", "Core/penguin-lsp.exe": b"MZ" + b"x" * 4096}
        self.check_zip(files, "visual-studio", True)
        files["Core/penguin-lsp.exe"] = "placeholder"
        self.check_zip(files, "visual-studio", False)

    def test_unsafe_entries_are_rejected(self):
        for path in ("../outside", "a/../outside", "/absolute", "C:/absolute", "a\\\\..\\\\outside"):
            with self.subTest(path=path):
                self.check_zip({path: "x"}, "vscode", False)

    def test_misplaced_visual_studio_entries_are_rejected(self):
        self.check_zip({"wrong/extension.vsixmanifest": "x", "wrong/PenguinExtention.dll": "x", "wrong/Core/penguin-lsp.exe": b"MZ" + b"x" * 4096}, "visual-studio", False)

    def test_case_colliding_entries_are_rejected(self):
        self.check_zip({"file.txt": "x", "FILE.TXT": "x"}, "vscode", False)

    def test_vscode_entrypoint_and_server(self):
        self.check_zip({"extension/package.json": '{"main":"./dist/extension.js"}', "extension/dist/extension.js": "x", "extension/bin/linux-x64/penguin-lsp": bytes([0x7f, 0x45, 0x4c, 0x46]) + b"x" * 4096}, "vscode", True)


if __name__ == "__main__":
    unittest.main()
