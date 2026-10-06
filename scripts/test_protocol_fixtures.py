"""Validate the minimal language-neutral transport fixtures."""
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1] / "docs/fixtures"


class ProtocolFixtures(unittest.TestCase):
    def load(self, name):
        return json.loads((ROOT / name).read_text(encoding="utf-8"))

    def test_symbol_shape_is_language_neutral(self):
        symbol = self.load("symbol.json")
        for key in ("id", "name", "kind", "macroName", "file"):
            self.assertIsInstance(symbol[key], str)
        self.assertGreaterEqual(symbol["line"], 1)
        self.assertIn(symbol["kind"], ["class", "struct", "interface", "enum", "function", "property", "delegate"])
        self.assertIsInstance(symbol["bases"], list)
        for specifier in symbol["specifiers"]:
            self.assertIsInstance(specifier["key"], str)
            self.assertTrue(specifier.get("value") is None or isinstance(specifier["value"], str))

    def test_ai_is_explicitly_disabled(self):
        options = self.load("initialize.json")["initializationOptions"]["penguin"]
        self.assertFalse(options["ai"]["enabled"])
        self.assertEqual(options["ai"]["endpoint"], "http://127.0.0.1:11434")
        self.assertEqual(options["ai"]["model"], "qwen2.5-coder:3b")

    def test_job_is_preview_data_and_terminal(self):
        job = self.load("job.json")
        self.assertEqual(job["state"], "succeeded")
        self.assertIsInstance(job["result"]["text"], str)
        self.assertNotIn("edits", job["result"])
        self.assertNotIn("command", job["result"])

    def test_status_version_is_explicit(self):
        status = self.load("status.json")
        self.assertEqual(status["protocolVersion"], 1)
        self.assertIsInstance(status["sessionId"], str)
        self.assertEqual(status["state"], "ready")


if __name__ == "__main__":
    unittest.main()
