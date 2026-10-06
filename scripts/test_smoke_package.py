import contextlib
import io
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import smoke_package


class PackagedSmokeChecks(unittest.TestCase):
    def package(self, folder, files):
        archive_path = Path(folder) / "package.vsix"
        with zipfile.ZipFile(archive_path, "w") as archive:
            for name, body in files.items():
                archive.writestr(name, body)
        return archive_path

    def test_extracts_only_expected_server_then_removes_copy(self):
        native = b"MZ" + b"x" * 4096
        with tempfile.TemporaryDirectory() as folder:
            archive = self.package(folder, {"extension.vsixmanifest": "x", "PenguinExtention.dll": "x", "Core/penguin-lsp.exe": native})
            copies = []

            def fake_smoke(binary, parent):
                copies.append(binary)
                self.assertEqual(binary.read_bytes(), native)
                self.assertEqual(list(binary.parent.iterdir()), [binary])
                self.assertEqual(parent, folder)

            with patch.object(smoke_package.platform, "system", return_value="Windows"), patch.object(smoke_package.platform, "machine", return_value="AMD64"), patch.object(smoke_package, "smoke", side_effect=fake_smoke), contextlib.redirect_stdout(io.StringIO()):
                smoke_package.run(archive, "visual-studio", folder)
            self.assertEqual(len(copies), 1)
            self.assertFalse(copies[0].exists())
            self.assertTrue(archive.exists())

    def test_wrong_host_never_executes_package(self):
        native = b"MZ" + b"x" * 4096
        with tempfile.TemporaryDirectory() as folder:
            archive = self.package(folder, {"extension.vsixmanifest": "x", "PenguinExtention.dll": "x", "Core/penguin-lsp.exe": native})
            with patch.object(smoke_package.platform, "system", return_value="Linux"), patch.object(smoke_package.platform, "machine", return_value="x86_64"), patch.object(smoke_package, "smoke") as mocked, contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaisesRegex(ValueError, "Windows x64"):
                    smoke_package.run(archive, "visual-studio", folder)
                mocked.assert_not_called()

    def test_invalid_package_never_executes(self):
        with tempfile.TemporaryDirectory() as folder:
            archive = self.package(folder, {"../escape": "x"})
            with patch.object(smoke_package, "smoke") as mocked:
                with self.assertRaises(ValueError):
                    smoke_package.run(archive, "vscode", folder)
                mocked.assert_not_called()


if __name__ == "__main__":
    unittest.main()
