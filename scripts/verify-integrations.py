"""Exercise MCP, browser and C# semantics through the real stdio Host and local SSE model.

Uses only generated files and loopback servers. Reuses the existing Host RPC and
process-identity helpers; performs no builds, paid model calls or desktop launch.
Chromium must already be installed through the pinned Playwright package.
"""
import argparse
import hashlib
import http.server
import importlib.util
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import textwrap
import threading
import uuid


ROOT = Path(__file__).resolve().parents[1]


def load_helper(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT / "scripts" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


stdio = load_helper("integration_verify_host", "verify-host.py")
fixtures = load_helper("integration_tools_helpers", "verify-tools-upgrade.py")
UNICODE = "MCP echo verified: 中文 😀"
FINAL = "MCP echo, browser assertions and C# overload references verified."
API = """namespace Integration;
public static class Api
{
    public static string Pick(int value) => "number";
    public static string Pick(string value) => "text";
}
"""
USE = """namespace Integration;
public sealed class Use
{
    public string Run()
    {
        int Pick = 7;
        // Api.Pick(123) is only a comment.
        string label = "😀"; return Api.Pick(Pick) + Api.Pick("hi");
    }
    public string Other() => Elsewhere.Api.Pick(1);
}
"""


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def position(source, needle):
    index = source.index(needle)
    start = source.rfind("\n", 0, index) + 1
    return {"line": source.count("\n", 0, index) + 1,
            "column": len(source[start:index].encode("utf-16-le")) // 2 + 1}


def verify_semantic(data, action):
    assert data["action"] == action and data["positionEncoding"] == "one-based UTF-16", data
    assert data["scope"]["sourceOnly"] and not data["scope"]["projectEvaluation"], data
    assert "int" in data["symbol"]["display"] and data["scannedFiles"] == 3, data
    expected = position(API, "Pick(int") if action == "definition" else position(USE, "Pick(Pick)")
    assert len(data["results"]) == 1, data
    actual = data["results"][0]
    assert actual["path"] == ("Api.cs" if action == "definition" else "Use.cs"), actual
    assert all(actual[key] == value for key, value in expected.items()), (actual, expected)
    assert actual["kind"] == ("definition" if action == "definition" else "reference"), actual


class FixtureHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        if self.path == "/favicon.ico":
            self.send_response(204)
            self.end_headers()
            return
        assert self.path == "/site"
        content = """<!doctype html><html><head><meta charset="utf-8">
        <title>Cogitide integration fixture</title></head><body><main>
        <h1 id="ready">Ready 就绪</h1><output id="async">Waiting</output></main>
        <script>setTimeout(() => { document.querySelector('#async').textContent = 'Async ready 异步就绪'; }, 50);</script>
        </body></html>""".encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

    def do_POST(self):
        try:
            assert self.path == "/v1/chat/completions"
            assert self.headers.get("Authorization") == "Bearer " + self.server.key
            length = int(self.headers["Content-Length"])
            assert 0 < length < 2_000_000
            body = json.loads(self.rfile.read(length))
            messages = body["messages"]
            start = max(i for i, message in enumerate(messages) if message["role"] == "user")
            prompt = messages[start]["content"]
            assert isinstance(prompt, str)
            stage = next(name for name in ("DENY", "APPROVED", "RESUME") if prompt.startswith("INTEGRATIONS_" + name))
            assert stage == self.server.stage
            names = {tool["function"]["name"] for tool in body["tools"]}
            assert "csharp_semantic" in names
            if stage == "RESUME":
                assert "browser_check" not in names and not any(name.startswith("mcp_") for name in names)
            else:
                remote = [name for name in names if name.startswith("mcp_fixture__echo_")]
                assert len(remote) == 1 and "browser_check" in names, names
                self.server.mcp_tool_name = remote[0]
            tail = messages[start + 1:]
            calls = {call["id"]: call for message in tail for call in message.get("tool_calls", [])}
            results = []
            for message in tail:
                if message["role"] == "tool":
                    call = calls[message["tool_call_id"]]
                    results.append((call["function"]["name"], message["content"]))
            action = self.choose(stage, results)
            self.server.requests += 1
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()

            def send(delta, finish=None):
                packet = {"id": "integration-fixture", "object": "chat.completion.chunk", "created": 1,
                          "model": "integration-fixture", "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]}
                self.wfile.write(("data: " + json.dumps(packet, ensure_ascii=False) + "\n\n").encode("utf-8"))
                self.wfile.flush()

            if action is None:
                text = "Denied MCP action was not executed." if stage == "DENY" else FINAL
                send({"role": "assistant", "content": text})
                send({}, "stop")
            else:
                name, arguments = action
                text = json.dumps(arguments, ensure_ascii=False)
                split = len(text) // 2
                send({"role": "assistant", "tool_calls": [{"index": 0, "id": stage.lower() + "_" + str(len(results)),
                     "type": "function", "function": {"name": name, "arguments": text[:split]}}]})
                send({"tool_calls": [{"index": 0, "function": {"arguments": text[split:]}}]})
                send({}, "tool_calls")
            self.wfile.write(b"data: [DONE]\n\n")
            self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception as error:
            self.server.errors.append(type(error).__name__ + ": " + str(error))
            try:
                self.send_error(500, "Local fixture contract failed")
            except (BrokenPipeError, ConnectionResetError):
                pass

    def choose(self, stage, results):
        if stage == "RESUME":
            assert not results
            return None
        name = self.server.mcp_tool_name
        if stage == "DENY":
            if not results:
                return name, {"value": "denied-probe"}
            assert len(results) == 1 and results[0][0] == name
            assert "declined" in results[0][1].lower(), results
            assert not (self.server.workspace / "call-started.txt").exists(), "Denied MCP call executed."
            return None
        if not results:
            return name, self.server.mcp_arguments
        if len(results) == 1:
            assert results[0][0] == name
            data = json.loads(results[0][1])
            assert not data["isError"] and data["structuredContent"]["echo"] == UNICODE, data
            environment = data["structuredContent"]["environment"]
            assert environment[self.server.provider_variable] is None
            assert environment[self.server.ambient_variable] is None
            assert environment[self.server.allowed_variable] == UNICODE
            return "browser_check", self.server.browser_arguments
        if len(results) == 2:
            assert results[-1][0] == "browser_check"
            data = json.loads(results[-1][1])
            assert data["outcome"] == "passed" and len(data["assertions"]) == 4, data
            assert all(assertion["passed"] for assertion in data["assertions"]), data
            return "csharp_semantic", dict(action="definition", path="Use.cs", **position(USE, "Pick(Pick)"))
        if len(results) == 3:
            assert results[-1][0] == "csharp_semantic"
            verify_semantic(json.loads(results[-1][1]), "definition")
            return "csharp_semantic", dict(action="references", path="Use.cs", **position(USE, "Pick(Pick)"))
        assert len(results) == 4 and results[-1][0] == "csharp_semantic", results
        verify_semantic(json.loads(results[-1][1]), "references")
        return None


class IntegrationHost(stdio.Host):
    """Use the established RPC client; approve only this fixture's exact requests."""
    def __init__(self, state, environment, server, owned):
        super().__init__(state, environment, auto_approve=False)
        self.fixture = server
        self.owned = owned
        self.decisions = []
        self.session_id = None
        self.diagnostics = []

        def drain_errors():
            for line in self.process.stderr:
                if len(self.diagnostics) < 100:
                    self.diagnostics.append(line.rstrip())
        threading.Thread(target=drain_errors, daemon=True).start()

    def next(self, timeout=120):
        item = super().next(timeout)
        if item.get("method") != "approval.requested":
            return item
        request = item["params"]
        arguments = json.loads(request["argumentsJson"])
        assert request["sessionId"] == self.session_id
        name = request["toolName"]
        stage = self.fixture.stage
        expected = False
        approved = False
        if name == self.fixture.mcp_tool_name:
            if stage == "DENY":
                expected = arguments == {"value": "denied-probe"}
            elif stage == "APPROVED":
                expected = approved = arguments == self.fixture.mcp_arguments
            if expected:
                pid = int((self.fixture.workspace / "fixture.pid").read_text(encoding="utf-8"))
                self.owned.append(fixtures.OwnedProcess(pid))
        elif stage == "APPROVED" and name == "browser_check":
            expected = approved = arguments == self.fixture.browser_arguments
        self.send("approval.respond", {"approvalId": request["id"], "approved": approved})
        assert expected, "Unexpected integration approval request: " + name
        self.decisions.append({"stage": stage, "tool": name, "approved": approved})
        return item


def start(host, session, stage, provider, readonly=False):
    return host.request("run.start", {"sessionId": session,
        "prompt": "INTEGRATIONS_" + stage + ": Execute only the deterministic integration fixture steps.",
        "provider": provider, "options": {"maxIterations": 10, "timeoutSeconds": 180,
        "maxContextCharacters": 300000, "autoCompact": False, "autoApproveWrites": False,
        "executionMode": "ReadOnly" if readonly else "WorkspaceWrite"}})["runId"]


def mcp_fixture_source():
    source = (ROOT / "tests/Workbench.Tests/McpIntegrationTests.cs").read_text(encoding="utf-8")
    match = re.search(r'private const string StdioFixture = """\n(.*?)\n        """;', source, re.S)
    assert match, "The shared MCP fixture template was not found."
    fixture = textwrap.dedent(match.group(1))
    old = "structured = {'environment':"
    assert fixture.count(old) == 1
    # Extend only the generated test peer, leaving the shared checked-in fixture intact.
    return fixture.replace(old, "structured = {'echo':args.get('value'),'environment':", 1)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--no-screenshot", action="store_true", help="Run browser assertions without creating a PNG.")
    args = parser.parse_args()
    directory = ROOT / "artifacts/verification/integrations" / uuid.uuid4().hex
    workspace = directory / "workspace"
    state = directory / "state"
    workspace.mkdir(parents=True)
    state.mkdir()
    generated = {"Api.cs": API, "Use.cs": USE,
        "Elsewhere.cs": "namespace Elsewhere; public static class Api { public static string Pick(int n) => n.ToString(); }\n",
        "notes.txt": "Generated integration input: retain this content. 用户备注。\n",
        "fixture.py": mcp_fixture_source()}
    for name, source in generated.items():
        (workspace / name).write_text(source, encoding="utf-8")
    subprocess.run(["git", "init", "-q"], cwd=workspace, check=True, capture_output=True)
    protected = {name: digest(workspace / name) for name in generated}
    provider_variable = "COGITIDE_INTEGRATION_MODEL_KEY_" + uuid.uuid4().hex.upper()
    ambient_variable = "COGITIDE_INTEGRATION_SECRET_" + uuid.uuid4().hex.upper()
    allowed_variable = "COGITIDE_INTEGRATION_ALLOWED_" + uuid.uuid4().hex.upper()
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), FixtureHandler)
    server.daemon_threads = True
    server.key = secrets.token_hex(24)
    server.workspace = workspace
    server.provider_variable = provider_variable
    server.ambient_variable = ambient_variable
    server.allowed_variable = allowed_variable
    server.stage = "DENY"
    server.errors = []
    server.requests = 0
    server.mcp_tool_name = None
    server.mcp_arguments = {"value": UNICODE, "inspect": [provider_variable, ambient_variable, allowed_variable]}
    server.browser_arguments = {"url": f"http://127.0.0.1:{server.server_port}/site", "timeoutSeconds": 30,
        "captureScreenshot": not args.no_screenshot,
        "assertions": [{"type": "text", "value": "Async ready 异步就绪"}, {"type": "selector", "value": "main #ready"},
            {"type": "title", "value": "Cogitide integration fixture"}, {"type": "status", "value": 200}]}
    threading.Thread(target=server.serve_forever, daemon=True).start()
    (state / "mcp.json").write_text(json.dumps({"servers": [{"id": "fixture", "transport": "stdio",
        "command": sys.executable, "arguments": ["-u", str(workspace / "fixture.py"), "normal", "fixture.pid"],
        "environmentVariables": [allowed_variable]}]}, indent=2), encoding="utf-8")
    environment = {provider_variable: server.key, ambient_variable: "parent-only-test-secret", allowed_variable: UNICODE}
    provider = {"endpoint": f"http://127.0.0.1:{server.server_port}/v1", "model": "integration-fixture",
        "apiKeyEnvironmentVariable": provider_variable, "provider": "OpenAiCompatible", "maxOutputTokens": 1500}
    managed_paths = sorted(stdio.HOST.parent.glob("Workbench.*.dll"))
    managed_paths += [stdio.HOST.parent / name for name in ("ModelContextProtocol.Core.dll", "Microsoft.Playwright.dll", "Microsoft.CodeAnalysis.CSharp.dll")]
    managed_hashes = {path.name: digest(path) for path in managed_paths}
    report = {"passed": False, "mode": "local-stdio-integrations-fixture", "workspace": str(workspace),
        "hostArtifact": str(stdio.HOST), "hostSha256": digest(stdio.HOST),
        "managedAssembliesSha256": managed_hashes, "checks": []}
    host = None
    owned = []
    decisions = []
    try:
        host = IntegrationHost(state, environment, server, owned)
        assert host.request("initialize", {})["protocolVersion"] == 1
        session = host.request("session.create", {"workspacePath": str(workspace), "title": "Integration acceptance", "backend": "api"})["info"]["id"]
        host.session_id = session
        denied_run = start(host, session, "DENY", provider)
        terminal = fixtures.wait_terminal(host, denied_run, 180)
        assert terminal["type"] == "run.completed", terminal
        assert not server.errors, server.errors
        assert len(owned) == 1
        fixtures.wait_until(lambda: not owned[0].alive(), 15)
        assert not (workspace / "call-started.txt").exists(), "Denied MCP call executed."
        denied_events = host.request("session.events", {"sessionId": session})
        denied_tools = [event for event in denied_events if event["type"] == "tool.completed"]
        assert len(denied_tools) == 1 and denied_tools[0]["result"]["isError"]
        assert "declined" in denied_tools[0]["result"]["content"].lower()
        assert host.decisions == [{"stage": "DENY", "tool": server.mcp_tool_name, "approved": False}]
        decisions += host.decisions
        denied_history = host.request("session.load", {"sessionId": session})
        host.stop()
        host = None
        report["checks"].append("MCP approval denial did not execute and owned process exited")

        server.stage = "APPROVED"
        host = IntegrationHost(state, environment, server, owned)
        host.session_id = session
        assert host.request("session.load", {"sessionId": session}) == denied_history
        approved_run = start(host, session, "APPROVED", provider)
        terminal = fixtures.wait_terminal(host, approved_run, 180)
        assert terminal["type"] == "run.completed", terminal
        assert not server.errors, server.errors
        assert len(owned) == 2
        fixtures.wait_until(lambda: not any(process.alive() for process in owned), 15)
        assert (workspace / "call-started.txt").read_text(encoding="utf-8").splitlines() == ["once"]
        history = host.request("session.load", {"sessionId": session})
        events = host.request("session.events", {"sessionId": session})
        completed = [event for event in events if event["runId"] == approved_run and event["type"] == "tool.completed"]
        assert [event["call"]["name"] for event in completed] == [server.mcp_tool_name, "browser_check", "csharp_semantic", "csharp_semantic"]
        assert all(not event["result"]["isError"] for event in completed), completed
        assert json.loads(completed[0]["result"]["content"])["structuredContent"]["echo"] == UNICODE
        browser = completed[1]
        assert json.loads(browser["result"]["content"])["outcome"] == "passed"
        screenshot = None
        if not args.no_screenshot:
            screenshot = Path(browser["result"]["artifactPath"])
            assert screenshot.resolve().is_relative_to(state.resolve()) and screenshot.is_file()
            assert screenshot.read_bytes().startswith(b"\x89PNG\r\n\x1a\n")
        for action, event in zip(("definition", "references"), completed[2:]):
            verify_semantic(json.loads(event["result"]["content"]), action)
        assert host.decisions == [{"stage": "APPROVED", "tool": server.mcp_tool_name, "approved": True},
                                  {"stage": "APPROVED", "tool": "browser_check", "approved": True}]
        assert history["messages"][-1]["content"] == FINAL
        decisions += host.decisions
        (directory / "events.json").write_text(json.dumps(events, ensure_ascii=False, indent=2), encoding="utf-8")
        pid_snapshot = (workspace / "fixture.pid").read_bytes()
        host.stop()
        host = None
        report["checks"] += ["approved MCP Unicode echo with provider and ambient credentials excluded",
            "headless browser local HTTP assertions and PNG" if screenshot else "headless browser local HTTP assertions",
            "Roslyn cross-file overload definition and reference with UTF-16 positions", "fresh per-run MCP process cleanup"]

        server.stage = "RESUME"
        host = IntegrationHost(state, environment, server, owned)
        host.session_id = session
        assert host.request("session.load", {"sessionId": session}) == history
        assert host.request("session.events", {"sessionId": session}) == events
        resumed_run = start(host, session, "RESUME", provider, readonly=True)
        terminal = fixtures.wait_terminal(host, resumed_run, 90)
        assert terminal["type"] == "run.completed", terminal
        assert not server.errors, server.errors
        assert not host.decisions and not any(event["type"] == "tool.started" for event in host.events)
        assert (workspace / "fixture.pid").read_bytes() == pid_snapshot
        assert (workspace / "call-started.txt").read_text(encoding="utf-8").splitlines() == ["once"]
        assert len(owned) == 2 and not any(process.alive() for process in owned)
        assert protected == {name: digest(workspace / name) for name in generated}, "Generated source was modified."
        assert managed_hashes == {path.name: digest(path) for path in managed_paths}, "Managed Host assemblies changed during verification."
        host.stop()
        host = None
        report["checks"].append("cold session/events persistence and read-only model resume without MCP reconnect or action replay")
        report.update(passed=True, sessionId=session, approvals=decisions, modelRequests=server.requests,
            toolCalls=[event["call"]["name"] for event in completed], ownedMcpPids=[process.pid for process in owned],
            protectedGeneratedSourcesSha256=protected, restartVerified=True, readOnlyResumeVerified=True,
            expectedDefinition={"path": "Api.cs", **position(API, "Pick(int")},
            expectedReference={"path": "Use.cs", **position(USE, "Pick(Pick)")},
            screenshot={"path": str(screenshot), "sha256": digest(screenshot)} if screenshot else None)
    except Exception as error:
        report["error"] = type(error).__name__ + ": " + str(error)
        report["fixtureErrors"] = server.errors
        raise
    finally:
        cleanup_errors = []
        if host is not None:
            try:
                host.stop()
            except Exception as error:
                cleanup_errors.append(type(error).__name__ + ": " + str(error))
        for process in owned:
            try:
                if process.alive():
                    cleanup_errors.append("MCP process required fixture cleanup: " + str(process.pid))
                process.cleanup()
            except Exception as error:
                cleanup_errors.append(type(error).__name__ + ": " + str(error))
        server.shutdown()
        server.server_close()
        if cleanup_errors:
            report["passed"] = False
            report["cleanupErrors"] = cleanup_errors
        (directory / "result.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps(report, ensure_ascii=False))
        if cleanup_errors:
            raise AssertionError("Integration cleanup failed: " + "; ".join(cleanup_errors))


if __name__ == "__main__":
    main()
