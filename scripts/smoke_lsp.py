"""Exercise a built or unpacked penguin-lsp executable without an IDE or AI model."""
import argparse
import hashlib
import json
from pathlib import Path
import queue
import subprocess
import tempfile
import threading
import time


class Session:
    def __init__(self, executable):
        self.process = subprocess.Popen([str(executable)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=None)
        self.responses = queue.Queue()
        self.next_id = 0
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        try:
            while True:
                headers = {}
                while True:
                    line = self.process.stdout.readline()
                    if not line:
                        raise EOFError("Language server closed stdout")
                    if line in (b"\r\n", b"\n"):
                        break
                    key, value = line.decode("ascii").strip().split(":", 1)
                    headers[key.lower()] = value.strip()
                length = int(headers["content-length"])
                if not 0 <= length <= 8 * 1024 * 1024:
                    raise ValueError("Unbounded protocol output")
                body = self.process.stdout.read(length)
                if len(body) != length:
                    raise EOFError("Truncated response")
                self.responses.put(json.loads(body))
        except Exception as error:
            self.responses.put(error)

    def send(self, method, params=None, request_id=None):
        message = {"jsonrpc": "2.0", "method": method}
        if params is not None:
            message["params"] = params
        if request_id is not None:
            message["id"] = request_id
        body = json.dumps(message, ensure_ascii=False).encode("utf-8")
        self.process.stdin.write(f"Content-Length: {len(body)}\r\n\r\n".encode("ascii") + body)
        self.process.stdin.flush()

    def request(self, method, params=None):
        self.next_id += 1
        self.send(method, params, self.next_id)
        deadline = time.monotonic() + 20
        while True:
            response = self.responses.get(timeout=max(0.001, deadline - time.monotonic()))
            if isinstance(response, Exception):
                raise response
            if response.get("id") == self.next_id:
                if "error" in response:
                    raise AssertionError(f"{method}: {response['error']}")
                return response.get("result")
            if time.monotonic() >= deadline:
                raise TimeoutError(method)

    def stop(self):
        if self.process.poll() is None:
            self.process.kill()
        self.process.wait(timeout=5)
        for stream in (self.process.stdin, self.process.stdout):
            if stream:
                stream.close()


def smoke(executable, temporary_parent=None):
    with tempfile.TemporaryDirectory(prefix="penguin-smoke-", dir=temporary_parent) as folder:
        root = Path(folder).resolve()
        header = root / "Example Actor.h"
        header.write_text("UCLASS(Blueprintable) class AExampleActor : public AActor {};\n", encoding="utf-8")
        before = hashlib.sha256(header.read_bytes()).digest()
        session = Session(executable)
        try:
            result = session.request("initialize", {"capabilities": {}, "workspaceFolders": [{"uri": root.as_uri(), "name": "Smoke fixture"}], "initializationOptions": {"penguin": {"adapter": "lsp", "ai": {"enabled": False}}}})
            assert result["capabilities"]["experimental"]["penguin"]["protocolVersion"] == 1, result
            session.send("initialized", {})
            job = session.request("penguin/reindex", {})
            deadline = time.monotonic() + 20
            while job["state"] in ("queued", "running"):
                if time.monotonic() >= deadline:
                    raise TimeoutError("Indexing did not finish")
                time.sleep(0.1)
                job = session.request("penguin/job", {"id": job["id"]})
                assert job is not None, "Index job expired before completion"
            assert job["state"] == "succeeded", job
            status = session.request("penguin/status", {})
            assert status["files"] == 1 and status["symbols"] >= 1, status
            symbols = session.request("penguin/symbols", {"query": "AExampleActor", "limit": 10})
            assert len(symbols) == 1 and symbols[0]["name"] == "AExampleActor", symbols
            detail = session.request("penguin/symbol", {"id": symbols[0]["id"]})
            assert detail["name"] == "AExampleActor", detail
            inheritance = session.request("penguin/inheritance", {"name": "AExampleActor"})
            assert "AActor" in inheritance["bases"], inheritance
            assert session.request("penguin/symbol", {"id": "stale-symbol-id"}) is None
            assert session.request("penguin/job", {"id": "unknown-job-id"}) is None
            session.send("textDocument/didOpen", {"textDocument": {"uri": header.as_uri(), "languageId": "cpp", "version": 1, "text": "UCLASS() class AUnsavedActor {};\n"}})
            outline = session.request("textDocument/documentSymbol", {"textDocument": {"uri": header.as_uri()}})
            assert any(row["name"] == "AUnsavedActor" for row in outline), outline
            assert session.request("workspace/symbol", {"query": "AExampleActor"}) == []
            assert session.request("penguin/symbols", {"query": "AExampleActor", "limit": 10}) == []
            live = session.request("penguin/symbols", {"query": "AUnsavedActor", "limit": 10})
            assert len(live) == 1 and live[0]["name"] == "AUnsavedActor", live
            assert session.request("penguin/symbol", {"id": live[0]["id"]})["name"] == "AUnsavedActor"
            session.send("textDocument/didChange", {"textDocument": {"uri": header.as_uri(), "version": 2}, "contentChanges": [{"text": ""}]})
            assert session.request("penguin/symbols", {"query": "AUnsavedActor", "limit": 10}) == []
            assert session.request("penguin/symbols", {"query": "AExampleActor", "limit": 10}) == []
            assert session.request("penguin/symbol", {"id": live[0]["id"]}) is None
            session.send("textDocument/didClose", {"textDocument": {"uri": header.as_uri()}})
            assert session.request("workspace/symbol", {"query": "AExampleActor"})
            assert session.request("shutdown") is None
            session.send("exit")
            assert session.process.wait(timeout=10) == 0, "Server did not exit cleanly with stdin open"
            assert hashlib.sha256(header.read_bytes()).digest() == before, "Server modified project source"
            print(f"PASS: {executable} initialize/index/status/details/live-overlay/close/shutdown; source unchanged")
        finally:
            session.stop()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path)
    parser.add_argument("--temp-dir", type=Path, help="Existing scratch directory for disposable fixture")
    args = parser.parse_args()
    executable = args.executable.resolve()
    if not executable.is_file():
        parser.error(f"Executable not found: {executable}")
    smoke(executable, args.temp_dir)
