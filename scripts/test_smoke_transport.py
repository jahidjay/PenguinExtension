"""Test smoke-test framing itself with a small in-memory peer."""
import importlib.util
from io import BytesIO
from pathlib import Path
import queue
from types import SimpleNamespace
import unittest

spec = importlib.util.spec_from_file_location("smoke_lsp", Path(__file__).with_name("smoke_lsp.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class FramingChecks(unittest.TestCase):
    def session(self):
        session = module.Session.__new__(module.Session)
        session.next_id = 0
        session.responses = queue.Queue()
        session.process = SimpleNamespace(stdin=BytesIO(), stdout=BytesIO())
        return session

    def test_utf8_content_length_round_trip(self):
        session = self.session()
        session.send("example", {"text": "雪🐧"}, 9)
        session.process.stdout = BytesIO(session.process.stdin.getvalue())
        session._read()
        message = session.responses.get_nowait()
        self.assertEqual(message["params"]["text"], "雪🐧")
        self.assertEqual(message["id"], 9)
        self.assertIsInstance(session.responses.get_nowait(), EOFError)

    def test_parameterless_exit_omits_params(self):
        session = self.session()
        session.send("exit")
        session.process.stdout = BytesIO(session.process.stdin.getvalue())
        session._read()
        message = session.responses.get_nowait()
        self.assertNotIn("params", message)
        self.assertNotIn("id", message)

    def test_non_protocol_stdout_is_an_error(self):
        session = self.session()
        session.process.stdout = BytesIO(b"accidental log" + bytes([10]))
        session._read()
        self.assertIsInstance(session.responses.get_nowait(), ValueError)


if __name__ == "__main__":
    unittest.main()
