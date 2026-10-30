"""Exercise the real stdio host. --live-deepseek opts into a small billable model run.

Default mode uses a deterministic local SSE provider and still executes real file,
approval, command, Git, persistence, and restart paths. No credential values are printed.
"""
import argparse
import base64
import hashlib
import http.server
import json
import os
from pathlib import Path
import queue
import subprocess
import struct
import sys
import threading
import time
import uuid
import zlib

ROOT = Path(__file__).resolve().parents[1]
DOTNET = ROOT / ".tools" / "dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")
HOST = ROOT / "src" / "Workbench.Host" / "bin" / "Debug" / "net11.0" / "Workbench.Host.dll"


class FixtureHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        results = [m for m in body["messages"] if m["role"] == "tool"]
        turn = len(results)
        calls = [
            ("read_file", {"path": "calculator.py"}),
            ("replace_text", {
                "path": "calculator.py", "oldText": "return a - b", "newText": "return a + b",
                "expectedSha256": json.loads(results[0]["content"])["sha256"] if results else "unused"
            }),
            ("run_command", {"command": self.server.verify_command, "timeoutSeconds": 30}),
            ("git_diff", {}),
            ("inspect_evidence", {})
        ]
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        def send(delta, finish=None):
            obj = {"id": "fixture-response", "object": "chat.completion.chunk", "created": 1,
                   "model": "fixture", "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}
            self.wfile.write(("data: " + json.dumps(obj) + "\n\n").encode())
            self.wfile.flush()
        if turn < len(calls):
            name, arguments = calls[turn]
            text = json.dumps(arguments)
            send({"role": "assistant", "tool_calls": [{"index": 0, "id": f"call_{turn}", "type": "function", "function": {"name": name, "arguments": text[:len(text)//2]}}]})
            send({"tool_calls": [{"index": 0, "function": {"arguments": text[len(text)//2:]}}]})
            send({}, "tool_calls")
        else:
            send({"content": "Fixed addition and verified the existing tests. User notes preserved."})
            send({}, "stop")
        self.wfile.write(b"data: [DONE]\n\n")


class Host:
    def __init__(self, data_dir, extra_env, auto_approve=True):
        env = dict(os.environ, **extra_env)
        if DOTNET.exists():
            env["DOTNET_ROOT"] = str(DOTNET.parent)
            env["PATH"] = str(DOTNET.parent) + os.pathsep + env.get("PATH", "")
        self.process = subprocess.Popen([str(DOTNET) if DOTNET.exists() else "dotnet", str(HOST), "--data-dir", str(data_dir)],
            cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, encoding="utf-8", env=env,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        self.messages = queue.Queue()
        self.events = []
        self.number = 0
        self.reply_cache = {}
        self.approvals = 0
        self.auto_approve = auto_approve
        self.pending_approvals = []
        def read():
            for line in self.process.stdout:
                self.messages.put(json.loads(line))
            self.messages.put({"closed": True})
        threading.Thread(target=read, daemon=True).start()

    def send(self, method, params):
        self.number += 1
        ident = str(self.number)
        self.process.stdin.write(json.dumps({"id": ident, "method": method, "params": params}) + "\n")
        self.process.stdin.flush()
        return ident

    def next(self, timeout=120):
        item = self.messages.get(timeout=timeout)
        if item.get("closed"):
            raise RuntimeError("Host exited before completing the protocol request.")
        if item.get("method") == "approval.requested":
            self.approvals += 1
            # This script owns a generated fixture and explicitly approves only its test edits/commands.
            if self.auto_approve:
                self.send("approval.respond", {"approvalId": item["params"]["id"], "approved": True})
            else:
                self.pending_approvals.append(item["params"])
        elif item.get("method") == "agent.event":
            self.events.append(item["params"])
        if "id" in item:
            self.reply_cache[item["id"]] = item
        return item

    def request(self, method, params, timeout=120):
        ident = self.send(method, params)
        deadline = time.monotonic() + timeout
        while ident not in self.reply_cache:
            self.next(max(0.1, deadline - time.monotonic()))
        reply = self.reply_cache.pop(ident)
        if reply.get("error"):
            raise RuntimeError(reply["error"])
        return reply.get("result")

    def stop(self):
        self.process.stdin.close()
        try:
            self.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait(timeout=5)
        if self.process.returncode:
            raise RuntimeError("Host returned a nonzero exit status.")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--live-deepseek", action="store_true")
    parser.add_argument("--thinking", choices=["none", "low", "high", "max"], default="none")
    parser.add_argument("--vision", action="store_true")
    args = parser.parse_args()
    if (args.thinking != "none" or args.vision) and not args.live_deepseek:
        parser.error("--thinking and --vision require --live-deepseek")
    if args.live_deepseek and not os.environ.get("DEEPSEEK_API_KEY"):
        raise SystemExit("DEEPSEEK_API_KEY is required; no credentials are read from application files.")
    run_dir = ROOT / "artifacts" / ("live-" if args.live_deepseek else "protocol-") / uuid.uuid4().hex
    repo = run_dir / "workspace"
    repo.mkdir(parents=True)
    (repo / "calculator.py").write_text("def add(a, b):\n    return a - b\n", encoding="utf-8")
    (repo / "verify.py").write_text("from calculator import add\nassert add(2, 3) == 5\nassert add(-4, 7) == 3\nassert add(0, 0) == 0\nprint('3 addition checks passed')\n", encoding="utf-8")
    (repo / "notes.txt").write_text("User-owned note: retain this exact content. 用户已有修改。\n", encoding="utf-8")
    original = {p: hashlib.sha256((repo / p).read_bytes()).hexdigest() for p in ("verify.py", "notes.txt")}
    for command in (["git", "init", "-q"], ["git", "add", "."], ["git", "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", "fixture"]):
        subprocess.run(command, cwd=repo, check=True, capture_output=True)
    verify_command = ("& '" + sys.executable.replace("'", "''") + "' verify.py") if os.name == "nt" else ("'" + sys.executable.replace("'", "'\\''") + "' verify.py")
    server = None
    extra_env = {}
    if args.live_deepseek:
        provider = {"endpoint": "https://api.deepseek.com", "model": "deepseek-flash", "apiKeyEnvironmentVariable": "DEEPSEEK_API_KEY",
                    "maxOutputTokens": 4096 if args.thinking != "none" else 1200, "provider": "DeepSeek", "reasoningEffort": args.thinking}
    else:
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), FixtureHandler)
        server.verify_command = verify_command
        threading.Thread(target=server.serve_forever, daemon=True).start()
        extra_env["WORKBENCH_FIXTURE_KEY"] = "local-fixture-only"
        provider = {"endpoint": f"http://127.0.0.1:{server.server_port}/v1", "model": "fixture", "apiKeyEnvironmentVariable": "WORKBENCH_FIXTURE_KEY", "maxOutputTokens": 1200}
    host = Host(run_dir / "state", extra_env)
    try:
        assert host.request("initialize", {})["protocolVersion"] == 1
        session = host.request("session.create", {"workspacePath": str(repo), "title": "Addition regression", "backend": "api"})
        ident = session["info"]["id"]
        assert "calculator.py" in host.request("workspace.files", {"workspacePath": str(repo)})
        prompt = ("Fix the add function in calculator.py. Read it first, change only the incorrect subtraction to addition, preserve notes.txt and verify.py exactly. "
                  "Run this existing verification command: " + verify_command + ". Inspect git_diff and report the verified outcome. Do not create extra files or alter tests.")
        images = []
        if args.vision:
            def chunk(kind, data):
                return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
            png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", 128, 128, 8, 2, 0, 0, 0))
            png += chunk(b"IDAT", zlib.compress((b"\x00" + b"\xff\x00\x00" * 128) * 128)) + chunk(b"IEND", b"")
            images = [{"mediaType": "image/png", "base64Data": base64.b64encode(png).decode(), "name": "color-fixture.png"}]
            prompt += " Also identify the dominant color in the attached image in your final answer (use an English color word)."
        accepted = host.request("run.start", {"sessionId": ident, "prompt": prompt, "images": images, "provider": provider,
            "options": {"maxIterations": 10, "timeoutSeconds": 240, "autoApproveWrites": False, "maxContextCharacters": 100000}})
        deadline = time.monotonic() + 260
        while not any(e["type"] in ("run.completed", "run.failed", "run.cancelled", "run.paused") for e in host.events):
            host.next(max(0.1, deadline - time.monotonic()))
        terminal = next(e for e in reversed(host.events) if e["type"].startswith("run."))
        assert terminal["type"] == "run.completed", terminal.get("text")
        assert terminal["runId"] == accepted["runId"]
        assert host.approvals >= 2, "Expected edit and command approval paths."
        verified = subprocess.run([sys.executable, "verify.py"], cwd=repo, capture_output=True, text=True, check=True)
        assert "3 addition checks passed" in verified.stdout
        for path, digest in original.items():
            assert hashlib.sha256((repo / path).read_bytes()).hexdigest() == digest, path + " unexpectedly modified"
        history = host.request("session.load", {"sessionId": ident})
        assert history["info"]["status"] == "completed"
        if args.vision:
            restored_images = history["messages"][0]["images"]
            assert len(restored_images) == len(images)
            assert all(all(actual.get(key) == value for key, value in expected.items()) for actual, expected in zip(restored_images, images))
            assert "red" in history["messages"][-1]["content"].lower(), "The live model did not identify the fixture color."
        continuation_count = sum(bool(m.get("continuation")) for m in history["messages"])
        if args.thinking != "none":
            assistants = [m for m in history["messages"] if m["role"] == "assistant"]
            assert all(isinstance((m.get("continuation") or {}).get("payload", {}).get("reasoning_content"), str) for m in assistants), "Missing live thinking state."
            assert any((m.get("continuation") or {}).get("payload", {}).get("reasoning_content") for m in assistants), "No live reasoning was observed."
        recorded = host.request("session.events", {"sessionId": ident})
        completed_tools = [e for e in recorded if e["type"] == "tool.completed"]
        assert all(not e["result"]["isError"] for e in completed_tools), [(e["call"]["name"], e["result"]["content"]) for e in completed_tools if e["result"]["isError"]]
        assert any(e["call"]["name"] == "run_command" and "3 addition checks passed" in e["result"]["content"] for e in completed_tools), "The agent did not produce successful verification evidence."
        evidence = host.request("session.evidence", {"sessionId": ident})
        assert evidence["entries"], "No durable tool evidence was projected."
        assert all(e["validity"] == "Stale" for e in evidence["entries"]), "Idle evidence must not claim freshness."
        command_entries = [e for e in evidence["entries"] if e["outcome"] == "CommandExitedZero"]
        assert command_entries, "No structured successful command observation was recorded."
        for observation in evidence["entries"]:
            source = next(e for e in recorded if e["sequence"] == observation["source"]["eventSequence"])
            assert hashlib.sha256(source["result"]["content"].encode()).hexdigest() == observation["source"]["resultSha256"]
        assert all(e["toolName"] != "inspect_evidence" for e in evidence["entries"]), "Recall must not create recursive evidence."
        if not args.live_deepseek:
            assert any(e["call"]["name"] == "inspect_evidence" and "CommandExitedZero" in e["result"]["content"] for e in completed_tools)
        approval_count = host.approvals
        sequences = [e["sequence"] for e in recorded]
        assert sequences == sorted(set(sequences))
        host.stop()
        host = Host(run_dir / "state", extra_env)
        restored = host.request("session.load", {"sessionId": ident})
        assert restored == history, "Session did not survive process restart."
        assert host.request("session.evidence", {"sessionId": ident}) == evidence, "Evidence projection changed after restart."
        resumed = False
        if args.thinking != "none":
            followup = host.request("run.start", {"sessionId": ident, "prompt": "Without using tools or changing files, give one sentence stating the previous verification result and its scope.",
                "provider": provider, "options": {"maxIterations": 2, "timeoutSeconds": 90}})
            deadline = time.monotonic() + 100
            while not any(e["runId"] == followup["runId"] and e["type"] in ("run.completed", "run.failed", "run.cancelled", "run.paused") for e in host.events):
                host.next(max(0.1, deadline - time.monotonic()))
            terminal = next(e for e in reversed(host.events) if e["runId"] == followup["runId"] and e["type"].startswith("run."))
            assert terminal["type"] == "run.completed", terminal.get("text")
            assert not any(e["type"] == "tool.started" for e in host.events), "Report-only resumed task unexpectedly executed tools."
            resumed = True
        report = {"mode": "live-deepseek" if args.live_deepseek else "local-sse-fixture", "passed": True,
            "model": provider["model"], "approvals": approval_count, "events": len(recorded),
            "toolCalls": [e["call"]["name"] for e in recorded if e["type"] == "tool.completed"],
            "verification": verified.stdout.strip(), "preservedUserFiles": list(original), "restartVerified": True,
            "thinking": args.thinking, "vision": args.vision, "continuationMessages": continuation_count, "resumedModelCall": resumed, "evidenceVerified": True,
            "workspace": str(repo), "sessionId": ident}
        # Store no request headers or credentials; this fixture contains only generated code.
        (run_dir / "result.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
        (run_dir / "events.json").write_text(json.dumps(recorded, indent=2, ensure_ascii=False), encoding="utf-8")
        print(json.dumps(report, ensure_ascii=False))
    except Exception as error:
        report = {"mode": "live-deepseek" if args.live_deepseek else "local-sse-fixture", "passed": False,
                  "thinking": args.thinking, "vision": args.vision, "error": type(error).__name__ + ": " + str(error), "workspace": str(repo)}
        (run_dir / "result.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
        raise
    finally:
        host.stop()
        if server:
            server.shutdown()


if __name__ == "__main__":
    main()
