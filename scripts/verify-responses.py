"""Verify the real stdio Host against a local native Responses SSE fixture.

No GUI, external account, real API key, Codex installation, or billable model call
is used. Only generated workspace files and an exact allowlisted command are approved.
Build the Debug Host first; this script intentionally never invokes the SDK build.
"""
import hashlib
import http.server
import importlib.util
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import threading
import time
import uuid


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("verify_host", ROOT / "scripts" / "verify-host.py")
stdio = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stdio)
FINAL = "Fixed addition; three generated checks passed."
PRIVATE = "fixture-encrypted-reasoning-only"


def event(kind, fields, sequence):
    return "event: " + kind + "\ndata: " + json.dumps(dict(fields, type=kind, sequence_number=sequence)) + "\n\n"


class ResponsesHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        try:
            assert self.path == "/v1/responses", "Expected native Responses endpoint."
            assert self.headers.get("Authorization") == "Bearer " + self.server.fixture_key, "Fixture credential mismatch."
            length = int(self.headers["Content-Length"])
            assert 0 < length < 2_000_000, "Unexpected fixture request size."
            body = json.loads(self.rfile.read(length))
            assert body["model"] == "fixture-responses"
            assert body["store"] is False and body["stream"] is True
            assert body["max_output_tokens"] == 1200
            assert "previous_response_id" not in body
            assert "reasoning.encrypted_content" in body["include"]
            assert body["input"][0]["role"] == "developer"
            assert all(tool["type"] == "function" for tool in body["tools"])
            offered = {tool["name"] for tool in body["tools"]}
            assert {"read_file", "replace_text", "run_command"} <= offered
            items = body["input"]
            results = [item for item in items if item.get("type") == "function_call_output"]
            # Replayed opaque native items must remain intact even after a Host restart.
            for item in items:
                if item.get("type") == "reasoning":
                    assert item["encrypted_content"] == PRIVATE
                if item.get("type") == "message" and item.get("id", "").startswith("msg_fixture_"):
                    assert item["phase"] == "final_answer"
            self.server.request_count += 1
            number = self.server.request_count
            if number > 1:
                assert any(item.get("type") == "reasoning" for item in items)
            if self.server.resumed:
                assert len(results) == 3
                assert any(item.get("phase") == "final_answer" for item in items)
                self.server.cold_replay_verified = True
            reason = {"type": "reasoning", "id": f"rs_fixture_{number}", "status": "completed",
                      "summary": [], "encrypted_content": PRIVATE}
            calls = [
                ("read_file", {"path": "calculator.py"}),
                ("replace_text", {"path": "calculator.py", "oldText": "return a - b", "newText": "return a + b",
                                  "expectedSha256": json.loads(results[0]["output"])["sha256"] if results else "unused"}),
                ("run_command", {"command": self.server.verify_command, "timeoutSeconds": 30}),
            ]
            wire = event("response.output_item.added", {"output_index": 0, "item": dict(reason, status="in_progress", encrypted_content=None)}, 1)
            wire += event("response.output_item.done", {"output_index": 0, "item": dict(reason, encrypted_content=None)}, 2)
            if len(results) < len(calls):
                name, arguments = calls[len(results)]
                arguments = json.dumps(arguments, separators=(",", ":"))
                item = {"type": "function_call", "id": f"fc_fixture_{number}", "call_id": f"call_fixture_{number}",
                        "name": name, "arguments": arguments, "status": "completed"}
                wire += event("response.output_item.added", {"output_index": 1, "item": dict(item, arguments="", status="in_progress")}, 3)
                wire += event("response.function_call_arguments.delta", {"output_index": 1, "item_id": item["id"], "delta": arguments}, 4)
            else:
                item = {"type": "message", "id": f"msg_fixture_{number}", "role": "assistant", "status": "completed",
                        "phase": "final_answer", "content": [{"type": "output_text", "text": FINAL, "annotations": []}]}
                wire += event("response.output_item.added", {"output_index": 1, "item": dict(item, status="in_progress", content=[])}, 3)
                wire += event("response.output_text.delta", {"output_index": 1, "item_id": item["id"], "content_index": 0, "delta": FINAL}, 4)
            wire += event("response.output_item.done", {"output_index": 1, "item": item}, 5)
            wire += event("response.completed", {"response": {"id": f"resp_fixture_{number}", "status": "completed", "output": [reason, item],
                          "usage": {"input_tokens": 100, "output_tokens": 25, "total_tokens": 125,
                                    "input_tokens_details": {"cached_tokens": 12}, "output_tokens_details": {"reasoning_tokens": 4}}}}, 6)
            payload = wire.encode()
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)
            self.wfile.flush()
        except Exception as error:
            self.server.errors.append(type(error).__name__ + ": " + str(error))
            self.send_error(500, "Local fixture validation failed")


class FixtureHost(stdio.Host):
    def __init__(self, data_dir, env, workspace, command):
        super().__init__(data_dir, env, auto_approve=False)
        self.workspace = workspace
        self.command = command
        self.approved_names = []

    def next(self, timeout=120):
        item = super().next(timeout)
        if item.get("method") == "approval.requested":
            request = item["params"]
            arguments = json.loads(request["argumentsJson"])
            name = request["toolName"]
            if name == "replace_text":
                assert arguments == {"path": "calculator.py", "oldText": "return a - b", "newText": "return a + b",
                                     "expectedSha256": digest(self.workspace / "calculator.py")}
            elif name == "run_command":
                assert arguments == {"command": self.command, "timeoutSeconds": 30}
            else:
                raise AssertionError("Unexpected approval; refusing fixture operation: " + name)
            self.approved_names.append(name)
            self.send("approval.respond", {"approvalId": request["id"], "approved": True})
        return item


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def await_run(host, run_id):
    deadline = time.monotonic() + 90
    terminal = {"run.completed", "run.failed", "run.cancelled", "run.paused"}
    while not any(event["runId"] == run_id and event["type"] in terminal for event in host.events):
        assert time.monotonic() < deadline, "Fixture run timed out."
        host.next(min(30, max(0.1, deadline - time.monotonic())))
    result = next(event for event in reversed(host.events) if event["runId"] == run_id and event["type"] in terminal)
    assert result["type"] == "run.completed", result.get("text")


def main():
    assert stdio.HOST.is_file(), "Build the Debug Host before running this verification."
    run_dir = ROOT / "artifacts" / "verification" / "unified-responses" / uuid.uuid4().hex
    workspace = run_dir / "workspace"
    workspace.mkdir(parents=True)
    (workspace / "calculator.py").write_text("def add(a, b):\n    return a - b\n", encoding="utf-8")
    (workspace / "verify.py").write_text("from pathlib import Path\nassert Path.cwd() == Path(__file__).resolve().parent\nfrom calculator import add\nassert add(2, 3) == 5\nassert add(-4, 7) == 3\nassert add(0, 0) == 0\nprint('3 addition checks passed')\n", encoding="utf-8")
    (workspace / "notes.txt").write_text("Preserve this generated note exactly. 保留。\n", encoding="utf-8")
    preserved = {name: digest(workspace / name) for name in ["verify.py", "notes.txt"]}
    command = "& '" + sys.executable.replace("'", "''") + "' verify.py" if os.name == "nt" else "'" + sys.executable.replace("'", "'\\''") + "' verify.py"
    # A restricted executable path proves the run does not depend on an installed Codex binary.
    windows = Path(os.environ.get("SystemRoot", "C:/Windows"))
    paths = [str(Path(sys.executable).parent)] + ([str(windows / "System32"), str(windows / "System32/WindowsPowerShell/v1.0")] if os.name == "nt" else ["/usr/bin", "/bin"])
    restricted_path = os.pathsep.join(paths)
    assert shutil.which("codex", path=restricted_path) is None, "Fixture PATH unexpectedly contains Codex."
    dependencies = json.loads(stdio.HOST.with_suffix(".deps.json").read_text(encoding="utf-8"))
    assert not any(name.startswith("Workbench.Codex/") for name in dependencies["libraries"])
    key = secrets.token_urlsafe(32)
    env = {"WORKBENCH_RESPONSES_FIXTURE_KEY": key, "PATH": restricted_path,
           "CODEX_HOME": str(run_dir / "unused-codex-home"), "DEEPSEEK_API_KEY": "", "OPENAI_API_KEY": "", "ANTHROPIC_API_KEY": ""}
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), ResponsesHandler)
    server.fixture_key, server.verify_command = key, command
    server.request_count, server.resumed, server.cold_replay_verified, server.errors = 0, False, False, []
    threading.Thread(target=server.serve_forever, daemon=True).start()
    provider = {"endpoint": f"http://127.0.0.1:{server.server_port}/v1", "model": "fixture-responses", "provider": "OpenAiResponses",
                "apiKeyEnvironmentVariable": "WORKBENCH_RESPONSES_FIXTURE_KEY", "maxOutputTokens": 1200, "reasoningEffort": "high"}
    host = None
    try:
        host = FixtureHost(run_dir / "state", env, workspace, command)
        initialized = host.request("initialize", {})
        assert initialized["executionEngine"] == "cogito" and initialized["chatGptAuthentication"] == "oauth"
        session = host.request("session.create", {"workspacePath": str(workspace), "title": "Native Responses fixture", "backend": "api"})
        ident = session["info"]["id"]
        run = host.request("run.start", {"sessionId": ident, "prompt": "Read calculator.py, fix addition, preserve verify.py and notes.txt, and execute the existing verification command.",
                           "provider": provider, "options": {"maxIterations": 6, "timeoutSeconds": 90, "autoApproveWrites": False}})
        await_run(host, run["runId"])
        assert host.approved_names == ["replace_text", "run_command"]
        history = host.request("session.load", {"sessionId": ident})
        assert history["info"]["status"] == "completed" and history["messages"][-1]["content"] == FINAL
        recorded = host.request("session.events", {"sessionId": ident})
        completed = [event for event in recorded if event["type"] == "tool.completed"]
        assert [event["call"]["name"] for event in completed] == ["read_file", "replace_text", "run_command"]
        assert all(not event["result"]["isError"] for event in completed)
        command_result = json.loads(completed[-1]["result"]["content"])
        assert command_result["exitCode"] == 0 and "3 addition checks passed" in command_result["output"]
        assert PRIVATE not in json.dumps(recorded)
        assert all(message.get("continuation", {}).get("format") == "openai-responses-v1" for message in history["messages"] if message["role"] == "assistant")
        evidence = host.request("session.evidence", {"sessionId": ident})
        assert any(entry["outcome"] == "CommandExitedZero" for entry in evidence["entries"])
        for name, expected in preserved.items():
            assert digest(workspace / name) == expected
        assert "return a + b" in (workspace / "calculator.py").read_text(encoding="utf-8")
        verified = subprocess.run([sys.executable, "verify.py"], cwd=workspace, capture_output=True, text=True, check=True)
        host.stop()
        host = None
        server.resumed = True
        host = FixtureHost(run_dir / "state", env, workspace, command)
        assert host.request("session.load", {"sessionId": ident}) == history
        assert host.request("session.events", {"sessionId": ident}) == recorded
        followup = host.request("run.start", {"sessionId": ident, "prompt": "Only report the prior verified outcome; no tool calls.",
                                "provider": provider, "options": {"maxIterations": 2, "timeoutSeconds": 90}})
        await_run(host, followup["runId"])
        assert not host.approved_names and not any(event["type"] == "tool.started" for event in host.events)
        assert server.cold_replay_verified and server.request_count == 5 and not server.errors, server.errors
        report = {"passed": True, "mode": "native-responses-local-stdio", "executionEngine": initialized["executionEngine"],
                  "toolCalls": [event["call"]["name"] for event in completed], "approvals": 2, "requests": server.request_count,
                  "verification": verified.stdout.strip(), "events": len(recorded), "persistedEventsVerified": True,
                  "coldResumeVerified": True, "nativeMetadataReplayVerified": True, "privateReasoningExcludedFromEvents": True,
                  "codexAbsentFromPath": True, "codexAssemblyDependency": False, "externalModelCalls": 0,
                  "workspace": str(workspace), "sessionId": ident}
        (run_dir / "events.json").write_text(json.dumps(recorded, indent=2, ensure_ascii=False), encoding="utf-8")
        (run_dir / "result.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
        print(json.dumps(report, ensure_ascii=False))
    except Exception as error:
        (run_dir / "result.json").write_text(json.dumps({"passed": False, "error": type(error).__name__ + ": " + str(error), "fixtureErrors": server.errors}, indent=2), encoding="utf-8")
        raise
    finally:
        if host is not None:
            host.stop()
        server.shutdown()
        server.server_close()


if __name__ == "__main__":
    main()
