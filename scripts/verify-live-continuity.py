"""One billable DeepSeek summary/resumption smoke over a cloned generated fixture.

Requires --live-deepseek and --source-state. Never use a user's real session store.
The source SQLite database and generated workspace are read-only test inputs.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import queue
import re
import shutil
import sqlite3
import time
import uuid

spec = importlib.util.spec_from_file_location("host_fixture", Path(__file__).with_name("verify-host.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
EXPECTED = "# external change\n"
ASSESSMENT = "The old repair is historical, not current verified success."


def fingerprint(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def source_rows(database):
    with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as connection:
        return (connection.execute("SELECT id,payload,updated_at FROM sessions ORDER BY id").fetchall(),
                connection.execute("SELECT session_id,sequence,payload FROM events ORDER BY session_id,sequence").fetchall())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--live-deepseek", action="store_true", help="Explicitly enable one billable model run.")
    parser.add_argument("--source-state", type=Path, required=True, help="Generated continuity fixture's state directory.")
    args = parser.parse_args()
    if not args.live_deepseek:
        parser.error("--live-deepseek is required; this check sends one bounded run to a paid model API.")
    secret = os.environ.get("DEEPSEEK_API_KEY")
    if not secret:
        raise SystemExit("DEEPSEEK_API_KEY is required; no credential files are read.")
    source_state = args.source_state.resolve()
    source_run = source_state.parent
    expected_parent = (fixture.ROOT / "artifacts" / "continuity").resolve()
    if source_state.name != "state" or source_run.parent != expected_parent or not re.fullmatch(r"[0-9a-f]{32}", source_run.name):
        raise SystemExit("Only an existing generated artifacts/continuity/<uuid>/state directory is accepted.")
    source_report = json.loads((source_run / "result.json").read_text(encoding="utf-8"))
    workspace = (source_run / "workspace").resolve()
    if workspace.parent != source_run:
        raise SystemExit("The generated workspace must not redirect outside its fixture directory.")
    if source_report.get("passed") is not True or source_report.get("mode") != "local-http-real-host" or Path(source_report["workspace"]).resolve() != workspace:
        raise SystemExit("The source must be a completed deterministic continuity fixture.")
    database = (source_state / "sessions.db").resolve()
    if database.parent != source_state:
        raise SystemExit("The database must remain inside its generated state directory.")
    code = workspace / "nested" / "code.py"
    if code.resolve() != code or any(not path.resolve().is_relative_to(workspace) for path in workspace.rglob("*")):
        raise SystemExit("Generated workspace paths must not redirect outside their original scope.")
    exact_content = code.read_bytes().decode("utf-8")
    if exact_content.replace("\r\n", "\n") != EXPECTED:
        raise SystemExit("This smoke requires the fixture's known external-change state.")
    original_workspace = {str(path.relative_to(workspace)): fingerprint(path) for path in workspace.rglob("*") if path.is_file()}
    original_rows = source_rows(database)
    eligible = [(ident, json.loads(payload)) for ident, payload, _ in original_rows[0]
                if len(json.loads(payload)["messages"]) > 20 and Path(json.loads(payload)["info"]["workspacePath"]).resolve() == workspace]
    if not eligible:
        raise SystemExit("No generated session with more than 20 messages was found.")
    session_id, baseline = max(eligible, key=lambda entry: len(entry[1]["messages"]))
    original_events = [json.loads(payload) for ident, _, payload in original_rows[1] if ident == session_id]
    out = fixture.ROOT / "artifacts" / "live-continuity" / uuid.uuid4().hex
    state = out / "state"
    state.mkdir(parents=True)
    with sqlite3.connect(database.as_uri() + "?mode=ro", uri=True) as source, sqlite3.connect(state / "sessions.db") as clone:
        source.backup(clone)
        cleared = dict(baseline, compaction=None)
        clone.execute("UPDATE sessions SET payload=? WHERE id=?", (json.dumps(cleared, ensure_ascii=False), session_id))
        clone.commit()
    # Use an isolated binary snapshot so simultaneous source builds cannot change or lock this smoke.
    built = fixture.HOST.parent
    expected_binaries = {name: fingerprint(built / name) for name in ("Workbench.Host.dll", "Workbench.Core.dll", "Workbench.Protocol.dll", "Workbench.Tools.dll")}
    runtime = out / "runtime"
    shutil.copytree(built, runtime)
    if any(fingerprint(runtime / name) != digest or fingerprint(built / name) != digest for name, digest in expected_binaries.items()):
        raise SystemExit("Host binaries changed during copy; no live API call was made.")
    fixture.HOST = runtime / "Workbench.Host.dll"
    provider = {"endpoint": "https://api.deepseek.com", "model": "deepseek-flash", "provider": "DeepSeek",
                "apiKeyEnvironmentVariable": "DEEPSEEK_API_KEY", "reasoningEffort": "none", "maxOutputTokens": 1600}
    prompt = ("Latest instruction: all fixture repair work in the preceding conversation is historical. "
              "nested/code.py was externally changed after that work. Do not continue the old repair. "
              "Read nested/code.py exactly once with read_file, then report its exact current text. "
              "Do not write files, execute commands, save memories, or run the old plan. "
              "Return only a JSON object with currentContent containing the exact text including its trailing newline, "
              "and assessment containing exactly this sentence: " + ASSESSMENT)
    report = {"passed": False, "mode": "live-deepseek-compaction-smoke", "model": "deepseek-flash", "thinking": "none",
              "sourceState": str(source_state), "workspace": str(workspace), "sessionId": session_id,
              "baselineMessages": len(baseline["messages"]), "runtimeSha256": expected_binaries, "maxIterations": 4,
              "maxOutputTokens": 1600, "timeoutSeconds": 150, "benchmark": False}
    host = None
    history = None
    recorded = []
    started = time.monotonic()

    def save(name, value):
        (out / name).write_text(json.dumps(value, ensure_ascii=False, indent=2).replace(secret, "[REDACTED]"), encoding="utf-8")

    try:
        print(json.dumps({"status": "live-run-starting", "artifact": str(out), "sourceMessages": len(baseline["messages"])}), flush=True)
        host = fixture.Host(state, {}, auto_approve=False)
        assert host.request("session.load", {"sessionId": session_id})["compaction"] is None
        accepted = host.request("run.start", {"sessionId": session_id, "prompt": prompt, "provider": provider,
            "options": {"maxIterations": 4, "timeoutSeconds": 150, "maxContextCharacters": 18000, "executionMode": "ReadOnly"}})
        report["runId"] = accepted["runId"]
        deadline = time.monotonic() + 165
        while True:
            terminal = [event for event in host.events if event["runId"] == accepted["runId"] and event["type"] in ("run.completed", "run.failed", "run.cancelled", "run.paused")]
            if terminal:
                report["terminal"] = terminal[-1]
                break
            if time.monotonic() >= deadline:
                raise TimeoutError("Bounded live run did not terminate in time.")
            try:
                host.next(min(30, max(0.1, deadline - time.monotonic())))
            except queue.Empty:
                continue
        history = host.request("session.load", {"sessionId": session_id})
        recorded = host.request("session.events", {"sessionId": session_id})
        run_events = [event for event in recorded if event["runId"] == accepted["runId"]]
        tools = [event for event in run_events if event["type"] == "tool.completed"]
        usage = [json.loads(event["text"]) for event in run_events if event["type"] == "model.usage"]
        report.update({"compactions": sum(event["type"] == "context.compacted" for event in run_events),
                       "toolNames": [event["call"]["name"] for event in tools], "usage": usage,
                       "fullHistoryMessages": len(history["messages"]), "approvals": host.approvals})
        assert terminal[-1]["type"] == "run.completed", "The single live attempt did not complete."
        assert report["compactions"] >= 1 and history["compaction"], "No live checkpoint was persisted."
        assert history["messages"][:len(baseline["messages"])] == baseline["messages"], "Original history or instructions changed."
        assert recorded[:len(original_events)] == original_events, "Original evidence changed."
        assert not any(event["call"]["name"] in {"write_file", "replace_text", "run_command", "remember_experience"} for event in tools), "Model attempted a prohibited action."
        reads = [event for event in tools if event["call"]["name"] == "read_file"]
        assert len(reads) == 1 and not reads[0]["result"]["isError"], "Expected one successful current-file read."
        assert json.loads(reads[0]["call"]["argumentsJson"])["path"] == "nested/code.py"
        assert json.loads(reads[0]["result"]["content"])["content"] == exact_content, "Read result did not preserve exact source bytes."
        final_text = history["messages"][-1]["content"].strip()
        if final_text.startswith("```json") and final_text.endswith("```"):
            final_text = final_text[7:-3].strip()
        final = json.loads(final_text)
        assert final["currentContent"] == exact_content and final["assessment"] == ASSESSMENT, "Final answer did not distinguish old claims from current content."
        assert host.approvals == 0
        host.stop()
        host = None
        host = fixture.Host(state, {}, auto_approve=False)
        assert host.request("session.load", {"sessionId": session_id}) == history, "Checkpoint/history did not survive restart."
        assert host.request("session.events", {"sessionId": session_id}) == recorded, "Events changed after restart."
        report["passed"] = True
        report["checks"] = ["real-model-summary", "read-only-current-file-recheck", "old-success-not-reused", "original-history-retained", "original-evidence-retained", "checkpoint-restart"]
    except Exception as error:
        report["error"] = (type(error).__name__ + ": " + str(error)).replace(secret, "[REDACTED]")
    finally:
        if host is not None:
            try:
                host.stop()
            except Exception as error:
                report["passed"] = False
                report["shutdownError"] = type(error).__name__
        report["seconds"] = round(time.monotonic() - started, 3)
        report["runtimeCoreMatchesCurrentDebug"] = fingerprint(built / "Workbench.Core.dll") == expected_binaries["Workbench.Core.dll"]
        report["sourceStoreUnchanged"] = source_rows(database) == original_rows
        report["workspaceUnchanged"] = original_workspace == {str(path.relative_to(workspace)): fingerprint(path) for path in workspace.rglob("*") if path.is_file()}
        report["passed"] &= report["sourceStoreUnchanged"] and report["workspaceUnchanged"]
        save("history.json", history)
        save("events.json", recorded)
        save("result.json", report)
        print(json.dumps({"result": str(out / "result.json"), **report}, ensure_ascii=False).replace(secret, "[REDACTED]"), flush=True)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
