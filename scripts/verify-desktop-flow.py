"""Opt-in, offline manual GUI regression fixture; never submits or approves UI actions.

    python scripts/verify-desktop-flow.py --prepare-only
    python scripts/verify-desktop-flow.py --launch --timeout-seconds 600

The launcher prints an isolated artifacts/desktop-flow/<id> directory containing
instructions.md, manifest.json and continuously refreshed result.json/events.json.
Close the Desktop window after reviewing the result. The loopback provider lives
only for this launch; do not reuse its saved endpoint after the launcher exits.
"""
import argparse
import hashlib
import http.server
import importlib.util
import json
import os
from pathlib import Path
import signal
import sqlite3
import subprocess
import sys
import threading
import time
import urllib.request
import uuid
from datetime import datetime, timezone


ROOT = Path(__file__).resolve().parents[1]
DOTNET = ROOT / ".tools" / "dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")
DESKTOP = ROOT / "src" / "Workbench.Desktop" / "bin" / "Debug" / "net11.0" / "Workbench.Desktop.dll"
HOST = ROOT / "src" / "Workbench.Host" / "bin" / "Debug" / "net11.0" / "Workbench.Host.dll"
FIXTURE_ENV = "WORKBENCH_FIXTURE_KEY"
EXPECTED_CALCULATOR = "def add(a, b):\n    return a + b\n"
CREATE_HIDDEN = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0


def utc_now():
    return datetime.now(timezone.utc).isoformat()


def write_json(path, value):
    temporary = path.with_name(path.name + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding="utf-8")
    temporary.replace(path)


def fixture_handler():
    spec = importlib.util.spec_from_file_location("workbench_host_fixture", ROOT / "scripts" / "verify-host.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.FixtureHandler


def prepare_fixture():
    run_dir = ROOT / "artifacts" / "desktop-flow" / uuid.uuid4().hex
    workspace = run_dir / "workspace"
    state = run_dir / "state"
    workspace.mkdir(parents=True, exist_ok=False)
    state.mkdir()
    (workspace / "calculator.py").write_text("def add(a, b):\n    return a - b\n", encoding="utf-8")
    (workspace / "verify.py").write_text(
        "from calculator import add\nassert add(2, 3) == 5\nassert add(-4, 7) == 3\n"
        "assert add(0, 0) == 0\nprint('3 addition checks passed')\n", encoding="utf-8")
    (workspace / "notes.txt").write_text("User-owned note: preserve this exact content. 用户已有修改。\n", encoding="utf-8")
    original = {name: hashlib.sha256((workspace / name).read_bytes()).hexdigest() for name in ("verify.py", "notes.txt")}
    for args in (["git", "init", "-q"], ["git", "add", "."],
                 ["git", "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", "fixture"]):
        subprocess.run(args, cwd=workspace, check=True, capture_output=True, creationflags=CREATE_HIDDEN)
    executable = sys.executable.replace("'", "''" if os.name == "nt" else "'\\''")
    invocation = ("& " if os.name == "nt" else "") + "'" + executable + "' verify.py"
    # Several meaningful lines make the command approval suitable for checking detail scrolling.
    verify_command = ("# Offline GUI fixture: run only the unchanged verification script.\n"
                      "# Preserve verify.py and notes.txt exactly.\n"
                      "# Expected evidence: 3 addition checks passed.\n" + invocation)
    prompt = ("修复 calculator.py 的 add 函数：先读取文件，只把错误的减法改成加法。"
              "保持 verify.py 和 notes.txt 完全不变。执行已有验证脚本，并检查 Git 差异。"
              "请在修改文件和运行命令前请求审批，最后报告实际验证结果。")
    return run_dir, workspace, state, original, verify_command, prompt


def read_evidence(run_dir, manifest):
    workspace = Path(manifest["workspace"])
    report = {"mode": "offline-manual-desktop", "checkedAt": utc_now(), "passed": False,
              "status": "awaiting-ui-task", "workspace": str(workspace), "sessionId": None,
              "approvalRequests": [], "toolCalls": [], "verification": None,
              "preservedUserFiles": [], "problems": []}
    events = []
    for name, digest in manifest["originalSha256"].items():
        path = workspace / name
        if path.exists() and hashlib.sha256(path.read_bytes()).hexdigest() == digest:
            report["preservedUserFiles"].append(name)
        else:
            report["problems"].append(name + " changed or is missing")
    database = Path(manifest["dataDirectory"]) / "sessions.db"
    if not database.exists():
        return report, events
    try:
        with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True, timeout=1) as connection:
            sessions = [json.loads(row[0]) for row in connection.execute("SELECT payload FROM sessions ORDER BY updated_at DESC")]
            candidates = [item for item in sessions if item["info"]["workspacePath"] == str(workspace)]
            if not candidates:
                return report, events
            # Clicking "New session" after a test must not hide the last executed run's evidence.
            session = candidates[0]
            for candidate in candidates:
                candidate_events = [json.loads(row[0]) for row in connection.execute(
                    "SELECT payload FROM events WHERE session_id=? ORDER BY sequence", (candidate["info"]["id"],))]
                if any(event["type"] == "run.started" for event in candidate_events):
                    session, events = candidate, candidate_events
                    break
            report["sessionId"] = session["info"]["id"]
            report["status"] = session["info"]["status"]
    except (sqlite3.Error, json.JSONDecodeError) as error:
        report["status"] = "waiting-for-readable-state"
        report["stateReadError"] = type(error).__name__
        return report, events
    latest_run = next((event["runId"] for event in reversed(events) if event["type"] == "run.started"), None)
    current = [event for event in events if event["runId"] == latest_run]
    approvals = [event for event in current if event["type"] == "approval.requested"]
    completed = [event for event in current if event["type"] == "tool.completed"]
    report["approvalRequests"] = [event["call"]["name"] for event in approvals]
    report["toolCalls"] = [event["call"]["name"] for event in completed]
    if any(event["type"] == "run.completed" for event in current):
        expected_tools = ["read_file", "replace_text", "run_command", "git_diff"]
        if report["toolCalls"] != expected_tools:
            report["problems"].append("Expected the complete read/edit/command/diff sequence")
        if not {"replace_text", "run_command"}.issubset(report["approvalRequests"]):
            report["problems"].append("Edit and command approvals were not both requested")
        for event in completed:
            if event["result"]["isError"]:
                report["problems"].append(event["call"]["name"] + " returned an error")
            if event["call"]["name"] == "run_command":
                try:
                    result = json.loads(event["result"]["content"])
                    if result.get("exitCode") == 0 and "3 addition checks passed" in result.get("output", ""):
                        report["verification"] = "3 addition checks passed"
                except (json.JSONDecodeError, TypeError):
                    pass
        if report["verification"] is None:
            report["problems"].append("No successful real command verification was recorded")
        calculator = workspace / "calculator.py"
        if not calculator.is_file() or calculator.read_text(encoding="utf-8") != EXPECTED_CALCULATOR:
            report["problems"].append("calculator.py does not contain the expected focused fix")
        report["passed"] = not report["problems"] and report["status"] == "completed"
    return report, events


def stop_desktop(process):
    if process is None or process.poll() is not None:
        return False
    # Normal close belongs to the person or Computer Use operating the window.
    # Deadline/error cleanup is process control only, and is explicitly reported as forced.
    if os.name == "nt":
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True,
                       creationflags=CREATE_HIDDEN, timeout=10, check=False)
    else:
        os.killpg(process.pid, signal.SIGTERM)
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
    process.wait(timeout=10)
    return True


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--launch", action="store_true", help="Explicitly open the real Desktop for manual UI verification")
    mode.add_argument("--prepare-only", action="store_true", help="Check the isolated fixture and local SSE response without opening a window")
    parser.add_argument("--timeout-seconds", type=int, default=600, help="Maximum GUI lifetime, 30-1800 seconds (default: 600)")
    args = parser.parse_args()
    if not 30 <= args.timeout_seconds <= 1800:
        parser.error("--timeout-seconds must be between 30 and 1800")
    if args.launch and not all(path.is_file() for path in (DOTNET, DESKTOP, HOST)):
        parser.error("Build the Debug Desktop and Host first; the private .tools/dotnet runtime is also required")
    run_dir, workspace, state, original, verify_command, prompt = prepare_fixture()
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), fixture_handler())
    server.daemon_threads = True
    server.verify_command = verify_command
    provider = {"endpoint": f"http://127.0.0.1:{server.server_port}/v1", "model": "fixture",
                "apiKeyEnvironmentVariable": FIXTURE_ENV, "maxOutputTokens": 1200}
    write_json(state / "desktop-settings.json", {"lastWorkspace": str(workspace), "backend": "api", "provider": provider})
    manifest = {"createdAt": utc_now(), "mode": "offline-manual-desktop", "workspace": str(workspace),
                "dataDirectory": str(state), "provider": provider, "prompt": prompt,
                "originalSha256": original, "desktopAssembly": str(DESKTOP), "timeoutSeconds": args.timeout_seconds}
    write_json(run_dir / "manifest.json", manifest)
    (run_dir / "instructions.md").write_text(
        "# 离线桌面审批流程验证\n\n此窗口使用本地假模型；保持 `fixture · API`，不要切换真实账号或供应商。\n\n"
        "1. 在输入框粘贴下面任务并发送。\n2. 审阅并允许 `replace_text`，检查文件与替换内容。\n"
        "3. 审阅并允许 `run_command`，检查完整多行命令。\n4. 确认最终回复、工具结果、Files/Changes 按钮和差异可查看。\n"
        "5. 查看同目录 `result.json` 是否 `passed: true`，然后关闭窗口。\n\n"
        "```text\n" + prompt + "\n```\n\n"
        "假模型只执行读取、修改、运行已有测试、Git diff 四步。没有自动提交任务或自动审批。"
        "`result.json` 验证后台证据；可见界面和滚动体验仍需人工检查。"
        "启动器退出后本地模型端口失效；再次验证请重新运行启动器。\n", encoding="utf-8")
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    process = None
    timed_out = False
    forced = False
    failure = None
    print(json.dumps({"fixtureDirectory": str(run_dir), "instructions": str(run_dir / "instructions.md"),
                      "result": str(run_dir / "result.json"), "prompt": prompt}, ensure_ascii=False), flush=True)
    try:
        if args.prepare_only:
            payload = json.dumps({"messages": [{"role": "user", "content": prompt}]}).encode("utf-8")
            request = urllib.request.Request(provider["endpoint"] + "/chat/completions", data=payload,
                                             headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(request, timeout=5) as response:
                stream = response.read().decode("utf-8")
            if '"name": "read_file"' not in stream or "data: [DONE]" not in stream:
                raise RuntimeError("The local SSE fixture did not return its first tool call")
            write_json(run_dir / "configuration-check.json", {"prepared": True, "localSseVerified": True,
                       "guiLaunched": False, "uiFlowVerified": False})
        else:
            env = dict(os.environ)
            for name in list(env):
                if name.upper().endswith("_API_KEY"):
                    env.pop(name)
            env[FIXTURE_ENV] = "local-fixture-only-" + uuid.uuid4().hex
            env["DOTNET_ROOT"] = str(DOTNET.parent)
            env["PATH"] = str(DOTNET.parent) + os.pathsep + env.get("PATH", "")
            with (run_dir / "desktop.stdout.log").open("wb") as stdout, (run_dir / "desktop.stderr.log").open("wb") as stderr:
                process = subprocess.Popen([str(DOTNET), str(DESKTOP), "--data-dir", str(state), "--workspace", str(workspace)],
                    cwd=ROOT, env=env, stdin=subprocess.DEVNULL, stdout=stdout, stderr=stderr,
                    creationflags=CREATE_HIDDEN, start_new_session=os.name != "nt")
                manifest["desktopPid"] = process.pid
                write_json(run_dir / "manifest.json", manifest)
                print(json.dumps({"desktopPid": process.pid, "fixtureDirectory": str(run_dir)}, ensure_ascii=False), flush=True)
                deadline = time.monotonic() + args.timeout_seconds
                while process.poll() is None and time.monotonic() < deadline:
                    report, events = read_evidence(run_dir, manifest)
                    write_json(run_dir / "result.json", report)
                    write_json(run_dir / "events.json", events)
                    time.sleep(0.5)
                timed_out = process.poll() is None
                forced = stop_desktop(process)
    except KeyboardInterrupt:
        failure = "Launcher interrupted; its owned Desktop process was terminated"
    except Exception as error:
        failure = f"{type(error).__name__}: {error}"
    finally:
        try:
            forced = stop_desktop(process) or forced
        except Exception as error:
            failure = f"Desktop cleanup failed: {type(error).__name__}: {error}"
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)
        report, events = read_evidence(run_dir, manifest)
        report.update({"guiLaunched": process is not None, "desktopExitCode": process.returncode if process else None,
                       "launcherTimedOut": timed_out, "forcedTermination": forced, "finishedAt": utc_now(),
                       "providerStopped": True})
        if failure:
            report["launcherError"] = failure
            report["passed"] = False
        if process and process.returncode != 0:
            report["passed"] = False
            report["problems"].append("Desktop did not exit successfully")
        write_json(run_dir / "events.json", events)
        write_json(run_dir / "result.json", report)
        print(json.dumps({"fixtureDirectory": str(run_dir), "result": str(run_dir / "result.json"),
                          "passed": report["passed"], "guiLaunched": process is not None,
                          "configurationVerified": args.prepare_only and failure is None,
                          "error": failure}, ensure_ascii=False), flush=True)
    return 0 if (args.prepare_only and failure is None) or report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
