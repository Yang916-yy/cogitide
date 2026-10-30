"""Exercise Host steering, workspace ownership, cancellation and crash recovery without a cloud model."""
import http.server
import importlib.util
import json
import os
import threading
import time
import uuid
from pathlib import Path

spec = importlib.util.spec_from_file_location("host_fixture", Path(__file__).with_name("verify-host.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        users = [m["content"] for m in body["messages"] if m["role"] == "user"]
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        if users[-1] == "Only explain; do not write files.":
            delta = {"content": "Additional instruction received; no file written."}
            finish = "stop"
        else:
            delta = {"role": "assistant", "tool_calls": [{"index": 0, "id": "call_" + uuid.uuid4().hex,
                "type": "function", "function": {"name": "write_file", "arguments": json.dumps({"path": "must-not-exist.txt", "content": "unexpected", "expectedSha256": ""})}}]}
            finish = "tool_calls"
        for content, reason in ((delta, None), ({}, finish)):
            packet = {"id": "fixture", "object": "chat.completion.chunk", "created": 1, "model": "fixture",
                "choices": [{"index": 0, "delta": content, "finish_reason": reason}]}
            self.wfile.write(("data: " + json.dumps(packet) + "\n\n").encode())
        self.wfile.write(b"data: [DONE]\n\n")


def wait_for(host, predicate, seconds=20):
    deadline = time.monotonic() + seconds
    while not predicate():
        host.next(max(0.1, deadline - time.monotonic()))


def expect_error(host, method, params, expected):
    try:
        host.request(method, params)
    except RuntimeError as error:
        assert expected.lower() in str(error).lower(), str(error)
    else:
        raise AssertionError(method + " unexpectedly accepted")


def main():
    root = fixture.ROOT / "artifacts" / "lifecycle" / uuid.uuid4().hex
    workspace = root / "workspace"
    workspace.mkdir(parents=True)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    env = {"WORKBENCH_FIXTURE_KEY": "local-only"}
    host = fixture.Host(root / "state", env, auto_approve=False)
    provider = {"endpoint": f"http://127.0.0.1:{server.server_port}/v1", "model": "fixture", "apiKeyEnvironmentVariable": "WORKBENCH_FIXTURE_KEY"}
    try:
        host.process.stdin.write("[]\n")
        host.process.stdin.flush()
        assert host.request("initialize", {})["protocolVersion"] == 1, "Malformed input must not terminate the Host."
        first = host.request("session.create", {"workspacePath": str(workspace)})["info"]["id"]
        second = host.request("session.create", {"workspacePath": str(workspace)})["info"]["id"]
        request = {"sessionId": first, "prompt": "Create a file.", "provider": provider, "options": {"maxIterations": 5, "timeoutSeconds": 60}}
        host.request("run.start", request)
        wait_for(host, lambda: len(host.pending_approvals) == 1)
        expect_error(host, "run.start", dict(request, sessionId=second), "workspace")
        host.request("run.steer", {"sessionId": first, "message": "Only explain; do not write files."})
        host.request("approval.respond", {"approvalId": host.pending_approvals[-1]["id"], "approved": True})
        wait_for(host, lambda: any(e["type"] == "run.completed" for e in host.events))
        assert not (workspace / "must-not-exist.txt").exists(), "Steering failed to skip the unstarted write."
        assert any(e["type"] == "input.steered" for e in host.events)
        expect_error(host, "run.steer", {"sessionId": first, "message": "too late"}, "active")
        host.events.clear()
        host.request("run.start", request)
        wait_for(host, lambda: len(host.pending_approvals) == 2)
        pending = host.pending_approvals[-1]["id"]
        host.request("run.cancel", {"sessionId": first})
        wait_for(host, lambda: any(e["type"] == "run.cancelled" for e in host.events))
        expect_error(host, "approval.respond", {"approvalId": pending, "approved": True}, "pending")
        assert not (workspace / "must-not-exist.txt").exists()
        host.events.clear()
        host.request("run.start", request)
        wait_for(host, lambda: len(host.pending_approvals) == 3)
        host.process.kill()
        host.process.wait(timeout=10)
        host = fixture.Host(root / "state", env, auto_approve=False)
        recovered = host.request("session.load", {"sessionId": first})
        assert recovered["info"]["status"] == "interrupted"
        assert not (workspace / "must-not-exist.txt").exists()
        assert host.approvals == 0
        # A user-started command owns its workspace too, and stop must bypass saturated work slots.
        command = "Start-Sleep -Seconds 5" if os.name == "nt" else "sleep 5"
        command_id = host.send("workspace.command", {"workspacePath": str(workspace), "command": command})
        expect_error(host, "run.start", request, "workspace")
        expect_error(host, "workspace.command", {"workspacePath": str(workspace), "command": "echo unexpected"}, "workspace")
        wait_for(host, lambda: command_id in host.reply_cache)
        assert not host.reply_cache.pop(command_id).get("error")
        pending = []
        for index in range(16):
            directory = root / ("load-" + str(index))
            directory.mkdir()
            pending.append(host.send("workspace.command", {"workspacePath": str(directory), "command": command}))
        expect_error(host, "initialize", {}, "busy")
        started = time.monotonic()
        assert host.request("run.cancel", {"sessionId": first}, timeout=3)["requested"]
        assert time.monotonic() - started < 3, "Stop was blocked behind work slots."
        wait_for(host, lambda: all(ident in host.reply_cache for ident in pending), seconds=20)
        for ident in pending:
            assert not host.reply_cache[ident].get("error")
        result = {"passed": True, "checks": ["workspace ownership", "steering before approved write", "cancel pending approval", "stale approval rejected", "crash recovery without replay", "manual command ownership", "responsive stop with 16 occupied work slots"], "sessionId": first}
        (root / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        print(json.dumps(result))
    finally:
        host.stop()
        server.shutdown()


if __name__ == "__main__":
    main()
