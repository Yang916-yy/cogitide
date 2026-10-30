"""Exercise upgraded tools through the real stdio Host and a deterministic local SSE model.

Uses only an isolated generated workspace: no desktop, cloud account, SDK build,
or user files. Approvals are exact allowlists. A deliberate Host hard crash checks
that its command worker observes stdin EOF and terminates the owned process tree.
Use --host PATH to exercise a published self-contained Host instead of Debug.
"""
import argparse
import ctypes
import hashlib
import http.server
import importlib.util
import json
import os
from pathlib import Path
import queue
import secrets
import shlex
import subprocess
import sys
import threading
import time
import uuid


ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("verify_host", ROOT / "scripts" / "verify-host.py")
stdio = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stdio)
TERMINALS = {"run.completed", "run.failed", "run.cancelled", "run.paused"}
PATCH = """*** Begin Patch
*** Update File: src/calculator.py
@@
 def add(a, b):
-    return a - b
+    return a + b
*** Update File: src/message.txt
@@
-state=old
+state=new
*** End Patch"""


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def command_for(script, marker):
    values = [sys.executable, script, marker]
    if os.name == "nt":
        return "& " + " ".join("'" + value.replace("'", "''") + "'" for value in values)
    # Keep /bin/sh present so the fixture can verify both the shell and its descendants.
    return " ".join(shlex.quote(value) for value in values) + "; status=$?; exit \"$status\""


class StrictHost(stdio.Host):
    def __init__(self, state, env, workspace, policy, executable=None):
        if executable is None:
            super().__init__(state, env, auto_approve=False)
        else:
            # Keep the shared fixture's protocol methods and exact event handling,
            # replacing only its launch command for the self-contained package.
            self.process = subprocess.Popen([str(executable), "--data-dir", str(state)], cwd=ROOT,
                stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                text=True, encoding="utf-8", env=dict(os.environ, **env),
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            self.messages = queue.Queue()
            self.events = []
            self.number = 0
            self.reply_cache = {}
            self.approvals = 0
            self.auto_approve = False
            self.pending_approvals = []

            def read():
                try:
                    for line in self.process.stdout:
                        self.messages.put(json.loads(line))
                finally:
                    self.messages.put({"closed": True})

            threading.Thread(target=read, daemon=True).start()
        self.workspace = workspace
        self.policy = policy
        self.approved = []
        self.diagnostics = []

        def read_errors():
            for line in self.process.stderr:
                if len(self.diagnostics) < 1000:
                    self.diagnostics.append(line.rstrip())

        threading.Thread(target=read_errors, daemon=True).start()

    def next(self, timeout=120):
        item = super().next(timeout)
        if item.get("method") != "approval.requested":
            return item
        request = item["params"]
        arguments = json.loads(request["argumentsJson"])
        name = request["toolName"]
        mode = self.policy["sessions"].get(request["sessionId"])
        for relative, expected in self.policy["protected"].items():
            assert digest(self.workspace / relative) == expected, "Generated verification input was changed: " + relative
        allowed = False
        if name == "apply_patch" and mode == "main":
            expected = {relative: digest(self.workspace / relative) for relative in ("src/calculator.py", "src/message.txt")}
            allowed = arguments == {"patchText": PATCH, "expectedHashes": expected}
        elif name == "start_command" and mode in ("main", "crash"):
            allowed = arguments == {"command": self.policy["commands"][mode], "timeoutSeconds": 120, "yieldMilliseconds": 0}
        self.send("approval.respond", {"approvalId": request["id"], "approved": allowed})
        assert allowed, "Rejected an unexpected fixture action: " + name
        self.approved.append({"tool": name, "mode": mode})
        return item


class FixtureHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        try:
            assert self.path == "/v1/chat/completions"
            assert self.headers.get("Authorization") == "Bearer " + self.server.key
            length = int(self.headers["Content-Length"])
            assert 0 < length < 2_000_000
            body = json.loads(self.rfile.read(length))
            messages = body["messages"]
            start = max(index for index, message in enumerate(messages) if message["role"] == "user")
            prompt = messages[start]["content"]
            assert isinstance(prompt, str)
            mode = next(name for name in ("main", "crash", "cold") if prompt.startswith("TOOLS_UPGRADE_" + name.upper()))
            definitions = {tool["function"]["name"] for tool in body["tools"]}
            if mode == "cold":
                assert len(definitions) == 15, definitions
                assert not definitions.intersection({"write_file", "replace_text", "apply_patch", "run_command", "start_command", "write_command", "create_worktree", "remove_worktree"})
            else:
                assert len(definitions) == 24, definitions
            self.server.tool_counts[mode] = len(definitions)
            tail = messages[start + 1:]
            calls = {call["id"]: call for message in tail for call in message.get("tool_calls", [])}
            results = []
            for message in tail:
                if message["role"] != "tool":
                    continue
                call = calls[message["tool_call_id"]]
                try:
                    data = json.loads(message["content"])
                except json.JSONDecodeError as error:
                    raise AssertionError("Tool did not return valid JSON: " + call["function"]["name"] + ": " + message["content"][:300]) from error
                results.append((call["function"]["name"], json.loads(call["function"]["arguments"]), data))
            action = self.choose(mode, results)
            self.server.requests += 1
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()

            def send(delta, finish=None):
                packet = {"id": "fixture-tools", "object": "chat.completion.chunk", "created": 1,
                          "model": "fixture-tools", "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}
                self.wfile.write(("data: " + json.dumps(packet) + "\n\n").encode())
                self.wfile.flush()

            if action is None:
                send({"role": "assistant", "content": "Fixture checks complete; recorded command status and file evidence were inspected."})
                send({}, "stop")
            else:
                name, arguments = action
                send({"role": "assistant", "tool_calls": [{"index": 0, "id": mode + "_" + str(len(results)), "type": "function",
                     "function": {"name": name, "arguments": json.dumps(arguments)}}]})
                send({}, "tool_calls")
            self.wfile.write(b"data: [DONE]\n\n")
        except (BrokenPipeError, ConnectionResetError):
            pass  # Expected when the separate Host process is deliberately killed.
        except Exception as error:
            self.server.errors.append(type(error).__name__ + ": " + str(error))
            try:
                self.send_error(500, "Fixture contract failed")
            except (BrokenPipeError, ConnectionResetError):
                pass

    def choose(self, mode, results):
        if mode == "cold":
            if not results:
                return "read_command", {"commandId": self.server.crash_id, "maxCharacters": 12000}
            assert results[-1][0] == "read_command"
            data = results[-1][2]
            assert data["status"] in ("cancelled", "interrupted"), data
            assert data["running"] is False and data["exitCode"] is None
            self.server.cold_log = data
            return None
        if mode == "crash":
            if not results:
                return "start_command", {"command": self.server.commands[mode], "timeoutSeconds": 120, "yieldMilliseconds": 0}
            data = results[-1][2]
            self.server.crash_id = data["commandId"]
            assert data["running"], data
            return "read_command", {"commandId": data["commandId"], "offset": data["nextOffset"], "waitMilliseconds": 1000}

        read_pages = [data for name, args, data in results if name == "read_file" and args["path"] == "src/long.txt"]
        if not read_pages:
            return "read_file", {"path": "src/long.txt", "limit": 1}
        last = read_pages[-1]
        assert all(page["sha256"] == self.server.long_hash for page in read_pages)
        if last["nextOffset"] is not None:
            assert last["truncated"]
            return "read_file", {"path": "src/long.txt", "offset": last["nextOffset"], "characterOffset": last["nextCharacterOffset"],
                                 "limit": 1, "expectedSha256": last["sha256"]}
        assert "".join(page["content"] for page in read_pages) == self.server.long_content
        assert len(read_pages) >= 3
        assert read_pages[0]["snapshotValidated"] is False
        assert all(page["snapshotValidated"] for page in read_pages[1:])
        self.server.read_pages = len(read_pages)
        reads = {args["path"]: data for name, args, data in results if name == "read_file"}
        for path in ("src/calculator.py", "src/message.txt"):
            if path not in reads:
                return "read_file", {"path": path}
        globs = [data for name, _, data in results if name == "glob_files"]
        if not globs:
            return "glob_files", {"pattern": "**/*.py"}
        assert globs[-1]["files"] == ["crash_child.py", "crash_tree.py", "src/calculator.py", "verify_task.py"], globs[-1]
        searches = [data for name, _, data in results if name == "search_files"]
        if not searches:
            return "search_files", {"query": "^def\\s+add", "regex": True, "glob": "**/*.py", "pathPrefix": "src/",
                                    "caseSensitive": True, "contextLines": 1}
        matches = searches[-1]["matches"]
        assert len(matches) == 1 and matches[0]["path"] == "src/calculator.py" and matches[0]["line"] == 1
        assert "return a - b" in matches[0]["after"][0]["text"]
        if not any(name == "apply_patch" for name, _, _ in results):
            return "apply_patch", {"patchText": PATCH, "expectedHashes": {path: reads[path]["sha256"] for path in ("src/calculator.py", "src/message.txt")}}
        command_pages = [data for name, _, data in results if name in ("start_command", "read_command")]
        if not command_pages:
            return "start_command", {"command": self.server.commands[mode], "timeoutSeconds": 120, "yieldMilliseconds": 0}
        data = command_pages[-1]
        self.server.main_id = data["commandId"]
        if data["running"] or data["hasMore"]:
            return "read_command", {"commandId": data["commandId"], "offset": data["nextOffset"], "maxCharacters": 2048,
                                    "waitMilliseconds": 1000 if data["running"] else 0}
        assert data["status"] == "completed" and data["exitCode"] == 0 and not data["outputTruncated"], data
        output = "".join(page["output"] for page in command_pages)
        assert output == self.server.expected_output, "Retained UTF-8 command pages lost or duplicated output."
        self.server.command_pages = len(command_pages)
        assert self.server.command_pages > 1
        return None


class OwnedProcess:
    """Hold Windows process handles; on Unix retain process start identity to avoid PID reuse."""
    def __init__(self, pid):
        self.pid = pid
        self.handle = None
        self.identity = None
        if os.name == "nt":
            self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
            self.kernel.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
            self.kernel.OpenProcess.restype = ctypes.c_void_p
            self.kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
            self.kernel.WaitForSingleObject.restype = ctypes.c_ulong
            self.kernel.TerminateProcess.argtypes = [ctypes.c_void_p, ctypes.c_uint]
            self.kernel.CloseHandle.argtypes = [ctypes.c_void_p]
            self.handle = self.kernel.OpenProcess(0x00100000 | 0x1000 | 0x1, False, pid)
            assert self.handle, "Could not capture generated process " + str(pid)
        else:
            self.identity = self._identity()
            assert self.identity, "Generated process exited before ownership capture."

    def _identity(self):
        proc = Path("/proc") / str(self.pid) / "stat"
        if proc.exists():
            fields = proc.read_text().rsplit(")", 1)[1].split()
            return None if fields[0] == "Z" else fields[19]
        result = subprocess.run(["ps", "-p", str(self.pid), "-o", "lstart=", "-o", "command="], capture_output=True, text=True)
        return result.stdout.strip() or None

    def alive(self):
        return self.kernel.WaitForSingleObject(self.handle, 0) == 0x102 if self.handle else self._identity() == self.identity

    def cleanup(self):
        if self.alive():
            if self.handle:
                self.kernel.TerminateProcess(self.handle, 1)
            else:
                import signal
                os.kill(self.pid, signal.SIGKILL)
        if self.handle:
            self.kernel.CloseHandle(self.handle)
            self.handle = None


def wait_until(predicate, seconds, host=None):
    deadline = time.monotonic() + seconds
    while not predicate():
        if time.monotonic() >= deadline:
            raise TimeoutError("Fixture condition did not complete within its deadline.")
        if host is not None:
            try:
                host.next(max(0.1, min(1, deadline - time.monotonic())))
            except queue.Empty:
                pass
        else:
            time.sleep(0.05)


def wait_terminal(host, run_id, seconds=90):
    wait_until(lambda: any(event["runId"] == run_id and event["type"] in TERMINALS for event in host.events), seconds, host)
    return next(event for event in reversed(host.events) if event["runId"] == run_id and event["type"] in TERMINALS)


def start(host, session, marker, provider, readonly=False):
    return host.request("run.start", {"sessionId": session, "prompt": "TOOLS_UPGRADE_" + marker,
        "provider": provider, "options": {"maxIterations": 60, "timeoutSeconds": 120,
        "maxContextCharacters": 500000, "autoCompact": False, "executionMode": "ReadOnly" if readonly else "WorkspaceWrite"}})["runId"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, help="Published self-contained Host executable; no SDK runtime is invoked.")
    args = parser.parse_args()
    executable = args.host.resolve() if args.host is not None else None
    if executable is not None and not executable.is_file():
        parser.error("Published Host executable does not exist: " + str(executable))
    if executable is None and not stdio.HOST.exists():
        raise SystemExit("Build the Debug Host before running this script. No build is performed here.")
    marker = uuid.uuid4().hex
    directory = ROOT / "artifacts" / "verification" / "tools-upgrade" / marker
    workspace = directory / "workspace"
    (workspace / "src").mkdir(parents=True)
    (workspace / "ignored").mkdir()
    (workspace / ".gitignore").write_text("ignored/\n", encoding="utf-8")
    (workspace / "ignored" / "secret.py").write_text("def add(a,b): return 'not discoverable'\n", encoding="utf-8")
    (workspace / "src" / "calculator.py").write_bytes(b"def add(a, b):\n    return a - b\n")
    (workspace / "src" / "message.txt").write_bytes(b"state=old\n")
    long_content = "a" * 11999 + "😀" + "中" * 6000 + "\r\nlast line\n"
    (workspace / "src" / "long.txt").write_bytes(long_content.encode("utf-8"))
    (workspace / "notes.txt").write_text("Generated protected note. 保持原文。\n", encoding="utf-8")
    expected_output = "UNICODE " + "中😀" * 7000 + "\nVERIFIED: 3 addition checks; both patch files match.\n"
    (workspace / "verify_task.py").write_text("""import hashlib, json, pathlib, sys, time
sys.stdout.reconfigure(encoding='utf-8', errors='strict', newline='\\n', write_through=True)
root = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(root / 'src'))
from calculator import add
assert add(2,3) == 5 and add(-4,7) == 3 and add(0,0) == 0
assert (root / 'src/message.txt').read_bytes() == b'state=new\\n'
report = {'checks': 3, 'marker': sys.argv[1], 'hashes': {name: hashlib.sha256((root/name).read_bytes()).hexdigest() for name in ('src/calculator.py', 'src/message.txt')}}
(root / 'verified.json').write_text(json.dumps(report), encoding='utf-8')
time.sleep(0.2)
sys.stdout.write('UNICODE ' + '中😀' * 7000 + '\\nVERIFIED: 3 addition checks; both patch files match.\\n')
""", encoding="utf-8")
    (workspace / "crash_child.py").write_text("""import pathlib, sys, time
root = pathlib.Path(__file__).resolve().parent
print('owned child alive ' + sys.argv[1], flush=True)
while True:
    (root / 'child-heartbeat.txt').write_text(str(time.time_ns()))
    time.sleep(0.1)
""", encoding="utf-8")
    (workspace / "crash_tree.py").write_text("""import json, os, pathlib, subprocess, sys, time
root = pathlib.Path(__file__).resolve().parent
with (root / 'crash-starts.log').open('a', encoding='utf-8') as log: log.write(sys.argv[1] + '\\n')
child = subprocess.Popen([sys.executable, str(root / 'crash_child.py'), sys.argv[1]], cwd=root)
record = {'shellPid': os.getppid(), 'pythonPid': os.getpid(), 'childPid': child.pid, 'marker': sys.argv[1]}
temporary = root / 'crash-pids.new'
temporary.write_text(json.dumps(record), encoding='utf-8')
temporary.replace(root / 'crash-pids.json')
print('owned command tree started ' + sys.argv[1], flush=True)
while True: time.sleep(0.2)
""", encoding="utf-8")
    git_env = {name: value for name, value in os.environ.items() if not name.upper().startswith("GIT_")}
    subprocess.run(["git", "init", "-q"], cwd=workspace, env=git_env, capture_output=True, check=True)
    protected = {name: digest(workspace / name) for name in ("notes.txt", "verify_task.py", "crash_tree.py", "crash_child.py", "src/long.txt", "ignored/secret.py")}
    commands = {"main": command_for("verify_task.py", marker), "crash": command_for("crash_tree.py", marker)}
    policy = {"sessions": {}, "commands": commands, "protected": protected}
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), FixtureHandler)
    server.daemon_threads = True
    server.key = secrets.token_hex(24)
    server.commands = commands
    server.errors = []
    server.requests = 0
    server.tool_counts = {}
    server.long_content = long_content
    server.long_hash = digest(workspace / "src/long.txt")
    server.expected_output = expected_output
    threading.Thread(target=server.serve_forever, daemon=True).start()
    env = {"COGITO_TOOLS_FIXTURE_KEY": server.key}
    provider = {"endpoint": f"http://127.0.0.1:{server.server_port}/v1", "model": "fixture-tools",
        "apiKeyEnvironmentVariable": "COGITO_TOOLS_FIXTURE_KEY", "provider": "OpenAiCompatible", "maxOutputTokens": 1200}
    host = None
    owned = []
    all_approvals = []
    host_artifact = executable or stdio.HOST
    managed_hashes = {path.name: digest(path) for path in sorted(host_artifact.parent.glob("Workbench.*.dll"))}
    # A framework-dependent/native apphost can stay byte-identical while its
    # managed tool implementation changes. Identify the code actually loaded.
    report = {"passed": False, "mode": "local-stdio-tools-fixture", "workspace": str(workspace),
              "hostArtifact": str(host_artifact), "hostSha256": digest(host_artifact),
              "managedAssembliesSha256": managed_hashes, "selfContained": executable is not None, "checks": []}
    try:
        host = StrictHost(directory / "state", env, workspace, policy, executable)
        init = host.request("initialize", {})
        assert len(init["tools"]) == 24
        session = host.request("session.create", {"workspacePath": str(workspace), "backend": "api"})["info"]["id"]
        policy["sessions"][session] = "main"
        run = start(host, session, "MAIN " + marker, provider)
        terminal = wait_terminal(host, run)
        assert terminal["type"] == "run.completed", terminal
        assert not server.errors, server.errors
        verified = json.loads((workspace / "verified.json").read_text(encoding="utf-8"))
        assert verified["checks"] == 3 and verified["marker"] == marker
        assert (workspace / "src/calculator.py").read_bytes() == b"def add(a, b):\n    return a + b\n"
        assert (workspace / "src/message.txt").read_bytes() == b"state=new\n"
        assert verified["hashes"] == {name: digest(workspace / name) for name in ("src/calculator.py", "src/message.txt")}
        events = host.request("session.events", {"sessionId": session})
        completed = [event for event in events if event["type"] == "tool.completed"]
        assert all(not event["result"]["isError"] for event in completed), completed
        assert [entry["tool"] for entry in host.approved] == ["apply_patch", "start_command"]
        (directory / "main-events.json").write_text(json.dumps(events, ensure_ascii=False, indent=2), encoding="utf-8")
        report["checks"] += ["paged read full hash and Unicode continuation", "regex/glob/context and Git ignore", "approved two-file patch", "approved background command with lossless UTF-8 output paging", "actual patched-file verification"]
        all_approvals += host.approved
        host.approved = []

        crash_session = host.request("session.create", {"workspacePath": str(workspace), "backend": "api"})["info"]["id"]
        policy["sessions"][crash_session] = "crash"
        crash_run = start(host, crash_session, "CRASH " + marker, provider)
        wait_until(lambda: any(event["type"] == "tool.completed" and event["call"]["name"] == "start_command" for event in host.events if event["runId"] == crash_run), 30, host)
        wait_until(lambda: (workspace / "crash-pids.json").exists() and (workspace / "child-heartbeat.txt").exists(), 20)
        # Capture an actual flushed tool page before the crash; file creation alone
        # does not prove either stdout pipe reached the retained log.
        wait_until(lambda: "owned command tree started" in "".join(
            json.loads(event["result"]["content"])["output"] for event in host.events
            if event["runId"] == crash_run and event["type"] == "tool.completed"
            and event["call"]["name"] in ("start_command", "read_command")) and
            "owned child alive" in "".join(
            json.loads(event["result"]["content"])["output"] for event in host.events
            if event["runId"] == crash_run and event["type"] == "tool.completed"
            and event["call"]["name"] in ("start_command", "read_command")), 20, host)
        pids = json.loads((workspace / "crash-pids.json").read_text(encoding="utf-8"))
        assert pids["marker"] == marker
        owned = [OwnedProcess(pids[name]) for name in ("shellPid", "pythonPid", "childPid")]
        assert all(process.alive() for process in owned)
        assert len({process.pid for process in owned}) == 3
        start_event = next(event for event in host.events if event["runId"] == crash_run and event["type"] == "tool.completed" and event["call"]["name"] == "start_command")
        server.crash_id = json.loads(start_event["result"]["content"])["commandId"]
        all_approvals += host.approved
        host.process.kill()  # Deliberately kill only the parent Host; do not help its worker kill the tree.
        host.process.wait(timeout=10)
        host.process.stdin.close()
        host = None
        wait_until(lambda: not any(process.alive() for process in owned), 20)
        heartbeat = (workspace / "child-heartbeat.txt").read_bytes()
        time.sleep(0.25)
        assert (workspace / "child-heartbeat.txt").read_bytes() == heartbeat
        report["checks"].append("hard Host crash: parent stdin EOF terminates shell and both generated descendants")

        requests_before_restart = server.requests
        host = StrictHost(directory / "state", env, workspace, policy, executable)
        host.request("initialize", {})
        restored = host.request("session.load", {"sessionId": crash_session})
        assert restored["info"]["status"] == "interrupted", restored["info"]
        assert server.requests == requests_before_restart, "Restart unexpectedly contacted the model."
        assert (workspace / "crash-starts.log").read_text(encoding="utf-8").splitlines() == [marker]
        cold_run = start(host, crash_session, "COLD " + marker, provider, readonly=True)
        assert wait_terminal(host, cold_run)["type"] == "run.completed"
        cold_events = host.request("session.events", {"sessionId": crash_session})
        cold_tools = [event["call"]["name"] for event in cold_events if event["runId"] == cold_run and event["type"] == "tool.started"]
        assert cold_tools == ["read_command"], cold_tools
        assert host.approvals == 0
        assert (workspace / "crash-starts.log").read_text(encoding="utf-8").splitlines() == [marker]
        assert "owned" in server.cold_log["output"], "Cold restart did not return retained command logs."
        assert all_approvals == [{"tool": "apply_patch", "mode": "main"},
            {"tool": "start_command", "mode": "main"}, {"tool": "start_command", "mode": "crash"}], all_approvals
        for relative, expected in protected.items():
            assert digest(workspace / relative) == expected, relative + " unexpectedly changed"
        assert not server.errors, server.errors
        (directory / "crash-and-cold-events.json").write_text(json.dumps(cold_events, ensure_ascii=False, indent=2), encoding="utf-8")
        report["checks"] += ["cold restart preserves interrupted chat and command logs without replay", "readonly exposes 12 tools and can inspect old command logs", "generated protected files unchanged"]
        report.update(passed=True, modelRequests=server.requests, toolCounts=server.tool_counts,
            readPages=server.read_pages, commandPages=server.command_pages, approvals=all_approvals,
            sessionId=session, crashSessionId=crash_session, commandId=server.main_id, crashedCommandId=server.crash_id,
            killedProcessIds=pids, restoredCommandStatus=server.cold_log["status"], outputCharacters=len(expected_output))
    except Exception as error:
        report["error"] = type(error).__name__ + ": " + str(error)
        report["fixtureErrors"] = server.errors
        raise
    finally:
        cleanup_errors = []
        if host is not None:
            try:
                if host.process.poll() is None:
                    host.stop()
            except Exception as error:
                cleanup_errors.append("Host: " + str(error))
            (directory / "last-host-events.json").write_text(json.dumps(host.events, ensure_ascii=False, indent=2), encoding="utf-8")
            (directory / "last-host-stderr.log").write_text("\n".join(host.diagnostics), encoding="utf-8")
        for process in owned:
            try:
                process.cleanup()
            except Exception as error:
                cleanup_errors.append("Fixture process " + str(process.pid) + ": " + str(error))
        try:
            server.shutdown()
            server.server_close()
        except Exception as error:
            cleanup_errors.append("Fixture server: " + str(error))
        if cleanup_errors:
            report.update(passed=False, cleanupErrors=cleanup_errors)
        (directory / "result.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps(report, ensure_ascii=False))
        if cleanup_errors and "error" not in report:
            raise AssertionError("Integration cleanup failed: " + "; ".join(cleanup_errors))


if __name__ == "__main__":
    main()
