"""Run real Host research paths against a deterministic loopback SSE model.

No SDK build, desktop automation, external model request or credential lookup occurs.
The fixture creates its own Git repositories and frozen validators. Its scores prove
integration and leakage guards, not research benchmark gains or model intelligence.
Usage: python scripts/verify-research.py [--host path/to/Workbench.Host.dll]
"""
import argparse
import hashlib
import http.server
import importlib.util
import json
import os
from pathlib import Path
import re
import shlex
import subprocess
import sys
import threading
import time
import traceback
import uuid

ROOT = Path(__file__).resolve().parents[1]
KEY = "COGITIDE_RESEARCH_FIXTURE_KEY"
LITERAL = "Always preserve verify.py and user notes."
INITIAL = LITERAL + "\nBASELINE_INSPECT_ONLY: inspect and report."
MARKER = "IMPROVED_FIXTURE: apply the requested isolated fix after reading its current hash."
CASES = {
    "train": ("calculator.py", "return a - b", "return a + b", "arithmetic", "Add two integers correctly."),
    "selection": ("formatter.py", "return left + '-' + right", "return left + ' ' + right", "formatting", "Join two words with one space."),
    "holdout": ("flags.py", "return value", "return not value", "boolean", "Negate a boolean value."),
}
VALIDATOR = '''import sys
from calculator import add
from formatter import join_words
from flags import negate
case = sys.argv[1]
if case == 'train':
    assert [add(a, b) for a, b in [(2, 3), (-4, 7), (0, -9)]] == [5, 3, -9]
elif case == 'selection':
    assert join_words('red', 'fox') == 'red fox'
    assert join_words('blue', 'sky') == 'blue sky'
elif case == 'holdout':
    assert negate(True) is False and negate(False) is True
else:
    raise AssertionError('Unknown frozen case')
print('Frozen ' + case + ' checks passed')
'''


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding="utf-8")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run(command, cwd, env=None, timeout=180):
    return subprocess.run(command, cwd=cwd, env=env, capture_output=True, text=True,
                          encoding="utf-8", timeout=timeout,
                          creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)


def git(repo, *arguments):
    result = run(["git", "-c", "core.hooksPath=" + str(repo / ".disabled-hooks"),
                  "-c", "core.fsmonitor=false", *arguments], repo)
    check(result.returncode == 0, "Fixture Git operation failed: " + result.stderr)
    return result.stdout.strip()


def commit(repo, message):
    git(repo, "add", "--all")
    git(repo, "-c", "user.name=Research Fixture", "-c", "user.email=fixture@example.invalid",
        "commit", "--quiet", "-m", message)
    return git(repo, "rev-parse", "HEAD")


def files_hashes(repo):
    return {p.relative_to(repo).as_posix(): digest(p) for p in repo.rglob("*")
            if p.is_file() and ".git" not in p.relative_to(repo).parts}


def make_repo(repo):
    repo.mkdir(parents=True)
    (repo / "calculator.py").write_text("def add(a, b):\n    return a - b\n", encoding="utf-8")
    (repo / "formatter.py").write_text("def join_words(left, right):\n    return left + '-' + right\n", encoding="utf-8")
    (repo / "flags.py").write_text("def negate(value):\n    return value\n", encoding="utf-8")
    (repo / "verify.py").write_text(VALIDATOR, encoding="utf-8")
    (repo / "notes.txt").write_text("User notes must remain byte-for-byte identical.\n", encoding="utf-8")
    (repo / "context.log").write_text("Historical observation " + "abcdef0123456789 " * 3000 + "\n", encoding="utf-8")
    git(repo, "init", "--quiet")
    return commit(repo, "Frozen independent arithmetic, formatting and boolean fixtures")


def command_for(case):
    # Validator invokes a known interpreter directly, not an agent-provided command.
    executable = str(Path(sys.executable).resolve())
    if os.name == "nt":
        return "& '" + executable.replace("'", "''") + "' -B verify.py " + case
    return shlex.quote(executable) + " -B verify.py " + case


def tasks(repo, base, kind):
    return [{"id": case, "family": item[3], "partition": case.title(), "payload": {
        "repository": str(repo), "baseCommit": base,
        "prompt": f"{kind}_CASE={case}. {item[4]} Change only {item[0]}; preserve verify.py and notes.txt. "
                  + ("Inspect context.log before editing. " if kind == "COMPRESSION" else "")
                  + f"Frozen {case} requirement token: {case.upper()}_ONLY_7831.",
        "validationCommands": [command_for(case)], "protectedPaths": ["verify.py", "notes.txt"]}}
        for case, item in CASES.items()]


def text_content(message):
    content = message.get("content") or ""
    if isinstance(content, list):
        return "".join(item.get("text", "") for item in content if isinstance(item, dict))
    return content


def tool_results(messages):
    calls = {call["id"]: call["function"] for message in messages
             for call in (message.get("tool_calls") or [])}
    return [(message.get("tool_call_id"), calls.get(message.get("tool_call_id"), {}), text_content(message))
            for message in messages if message.get("role") == "tool"]


class FixtureHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        try:
            check(self.path == "/v1/chat/completions", "Unexpected fixture route: " + self.path)
            check(self.headers.get("Authorization") == "Bearer fixture-only-not-a-secret", "Unexpected fixture authentication")
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            self.server.requests.append(body)
            response = self.respond(body)
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()

            def send(delta=None, finish=None, usage=False):
                value = {"id": "research-fixture", "object": "chat.completion.chunk", "created": 1,
                         "model": "research-fixture", "choices": [] if usage else [
                             {"index": 0, "delta": delta or {}, "finish_reason": finish}]}
                if usage:
                    value["usage"] = {"prompt_tokens": 100, "completion_tokens": 20, "total_tokens": 120}
                self.wfile.write(("data: " + json.dumps(value) + "\n\n").encode())
                self.wfile.flush()

            if isinstance(response, tuple):
                ident, name, arguments = response
                encoded = json.dumps(arguments)
                half = len(encoded) // 2
                send({"role": "assistant", "tool_calls": [{"index": 0, "id": ident, "type": "function",
                       "function": {"name": name, "arguments": encoded[:half]}}]})
                send({"tool_calls": [{"index": 0, "function": {"arguments": encoded[half:]}}]})
                send(finish="tool_calls")
            else:
                send({"role": "assistant", "content": response})
                send(finish="stop")
            send(usage=True)
            self.wfile.write(b"data: [DONE]\n\n")
            self.wfile.flush()
        except Exception:
            self.server.errors.append(traceback.format_exc())
            try:
                self.send_error(500, "Deterministic fixture assertion failed")
            except (BrokenPipeError, ConnectionResetError):
                pass

    def respond(self, body):
        messages = body["messages"]
        system = "\n".join(text_content(m) for m in messages if m["role"] == "system")
        users = "\n".join(text_content(m) for m in messages if m["role"] == "user")
        results = tool_results(messages)
        if "offline natural-language skill editor" in system:
            request = json.loads(text_content(messages[-1]))
            strategy = request["strategy"]
            check(strategy in ("Gepa", "SkillOpt"), "Unknown optimization strategy")
            check(("Pareto" in system) == (strategy == "Gepa"), "Strategy-specific proposer policy was not applied")
            observations = request["trainingObservations"]
            check(observations and all(o["taskId"] == "train" for o in observations), "Non-training task leaked into proposal")
            check("SELECTION_ONLY_7831" not in users and "HOLDOUT_ONLY_7831" not in users, "Held-out payload leaked into proposal")
            self.server.proposals.append(strategy)
            return json.dumps({"skill": request["parents"][0]["text"] + "\n" + MARKER,
                               "reflection": "The failed arithmetic check requires an observed, narrowly scoped edit."})
        if "Propose one revision to a coding agent's context-compression guideline" in system:
            request = json.loads(text_content(messages[-1]))
            check(all(item["datasetSplit"] == "development" for item in request["failures"]), "Proposal split leakage")
            return json.dumps({"historyInstructions": "Preserve unresolved requested edits. FIXTURE_KEEP_ACTION.",
                               "observationInstructions": "Keep paths, hashes, cursors and failures.",
                               "observationCharacterLimit": 1200, "rationale": "The development summary lost the pending edit."})
        if system.startswith("You summarize a coding conversation"):
            request = json.loads(text_content(messages[-1]))
            guidance = request.get("compressionGuidance", {})
            retained = "FIXTURE_KEEP_ACTION" in guidance.get("historyInstructions", "")
            self.server.summaries.append({"revision": guidance.get("revision"), "retained": retained})
            return "## Goal\nContinue task.\n## Progress\nInspected context.\n## Next Steps\n" + (
                "FIXTURE_ACTION_RETAINED: finish the requested edit." if retained else "Report current observations.")
        if "RUNTIME_RESEARCH" in users:
            calls = [
                ("runtime-search", "search_files", {"query": "RUNTIME_NEEDLE", "pathPrefix": "src/", "glob": "**/*.cs", "limit": 5}),
                ("runtime-read", "read_file", {"path": "src/feature.cs"}),
                ("runtime-memory", "repository_memory", {"query": "RUNTIME_NEEDLE", "seedPaths": ["src/feature.cs"], "maxCommits": 8}),
                ("runtime-large", "read_file", {"path": "context.log"}),
            ]
            return calls[len(results)] if len(results) < len(calls) else "Runtime research fixture completed."
        match = re.search(r"(SKILL|COMPRESSION)_CASE=(train|selection|holdout)", users)
        check(match is not None, "Unrecognized agent task")
        kind, case = match.groups()
        if kind == "COMPRESSION":
            checkpoint = "Historical context checkpoint" in users
            if not checkpoint:
                prior = [int(ident.split("-")[-1]) for ident, _, _ in results if ident and ident.startswith("compress-read-")]
                step = max(prior, default=-1) + 1
                check(step < 9, "Runtime never created a bounded checkpoint")
                return f"compress-read-{step}", "read_file", {"path": "context.log"}
            if "FIXTURE_ACTION_RETAINED" not in users:
                return "Baseline summary contains no retained edit instruction; inspect-only fixture outcome."
        elif MARKER not in users:
            return "Baseline inspection finished; no file was changed."
        path, old, new, _, _ = CASES[case]
        ids = {ident for ident, _, _ in results}
        if "guard-probe" not in ids:
            return "guard-probe", "write_file", {"path": "verify.py", "content": "print('forged validator')\n",
                                                 "expectedSha256": self.server.validator_hash}
        denied = next(content for ident, _, content in results if ident == "guard-probe")
        check("declin" in denied.lower() or "denied" in denied.lower() or "not approved" in denied.lower(),
              "Protected validator mutation was not denied: " + denied)
        if "target-read" not in ids:
            return "target-read", "read_file", {"path": path}
        if "target-patch" not in ids:
            observed = json.loads(next(content for ident, _, content in results if ident == "target-read"))
            return "target-patch", "apply_patch", {
                "patchText": f"*** Begin Patch\n*** Update File: {path}\n@@\n-    {old}\n+    {new}\n*** End Patch",
                "expectedHashes": {path: observed["sha256"]}}
        return "Requested change applied; frozen evaluator will independently check it."


def load_helper(host_path):
    spec = importlib.util.spec_from_file_location("verify_host_fixture", ROOT / "scripts" / "verify-host.py")
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    helper.HOST = host_path
    return helper


def batch(helper, spec, path, output, mode, execute, env):
    write_json(path, spec)
    command = [str(helper.DOTNET) if helper.DOTNET.exists() else "dotnet", str(helper.HOST),
               mode, str(path), "--output", str(output), "--execute" if execute else "--validate-only"]
    result = run(command, ROOT, env, timeout=240)
    path.with_suffix(".stdout.txt").write_text(result.stdout, encoding="utf-8")
    path.with_suffix(".stderr.txt").write_text(result.stderr, encoding="utf-8")
    check(result.returncode == 0, f"{mode} failed ({result.returncode}); inspect {path.with_suffix('.stderr.txt')}")
    return json.loads(result.stdout.strip().splitlines()[-1])


def verify_skill(output, original):
    report = json.loads((output / "optimization" / "report.json").read_text(encoding="utf-8"))
    selected = (output / "optimization" / "selected-skill.md").read_text(encoding="utf-8")
    check(report["status"] == "completed" and report["holdoutCompleted"], "Optimization did not finish its holdout")
    check(not report["automaticallyActivated"] and MARKER in selected, "Candidate missing or automatically activated")
    check(hashlib.sha256(selected.encode()).hexdigest() == report["selectedSkillSha256"], "Selected artifact hash mismatch")
    check(report["selectedSkillSha256"] != report["initialSkillSha256"], "Improved candidate was not selected")
    check(any(d["action"] == "selection_frozen" and d["reason"] == "holdout_is_report_only" for d in report["decisions"]),
          "Selection was not frozen before holdout")
    evaluations = report["evaluations"]
    held = [e for e in evaluations if e["partition"].lower() == "holdout"]
    check(len(evaluations) == 6 and len(held) == 2, "Expected paired baseline/candidate across three distinct tasks")
    first_holdout = next(i for i, e in enumerate(evaluations) if e["partition"].lower() == "holdout")
    check(all(e["partition"].lower() == "holdout" for e in evaluations[first_holdout:]), "Holdout influenced earlier selection")
    for evaluation in evaluations:
        expected = 1 if evaluation["candidateId"] == report["selectedCandidateId"] else 0
        check(evaluation["result"]["score"] == expected and evaluation["result"]["constraintsSatisfied"],
              "Frozen validator did not distinguish real candidate edits from baseline inspection")
    check(report["usage"]["allTokenUsageKnown"] and report["usage"]["allModelCallsKnown"], "Usage accounting was incomplete")
    for folder in (output / "evaluations").iterdir():
        workspace = folder / "workspace"
        check(digest(workspace / "verify.py") == original["verify.py"], "Protected validator changed")
        check(digest(workspace / "notes.txt") == original["notes.txt"], "Protected notes changed")
        events = json.loads((folder / "events.json").read_text(encoding="utf-8"))
        result = json.loads((folder / "result.json").read_text(encoding="utf-8"))
        if result["score"] == 1:
            check(any(e["type"] == "tool.completed" and (e.get("call") or {}).get("name") == "apply_patch"
                      and not e["result"]["isError"] for e in events), "Winning score lacks actual patch evidence")
            check(any(e["type"] == "tool.completed" and (e.get("call") or {}).get("id") == "guard-probe"
                      and e["result"]["isError"] for e in events), "Protected write denial was not recorded")
    return {"strategy": report["strategy"], "selectedSkillSha256": report["selectedSkillSha256"],
            "holdoutScores": [e["result"]["score"] for e in held], "usage": report["usage"],
            "report": str(output / "optimization" / "report.json")}


def verify_runtime(helper, root, provider, server):
    repo = root / "runtime-workspace"
    base = make_repo(repo)
    (repo / "src").mkdir()
    (repo / "src" / "feature.cs").write_text("// RUNTIME_NEEDLE baseline source\nclass Feature { }\n", encoding="utf-8")
    base = commit(repo, "RUNTIME_NEEDLE baseline feature")
    future = "FUTURE_COMMIT_MUST_NOT_LEAK_9917"
    (repo / "future-secret.txt").write_text(future, encoding="utf-8")
    future_sha = commit(repo, "RUNTIME_NEEDLE " + future)
    before = files_hashes(repo)
    host = helper.Host(root / "runtime-state", {KEY: "fixture-only-not-a-secret"}, auto_approve=False)
    errors = []
    threading.Thread(target=lambda: errors.extend(host.process.stderr.readlines()), daemon=True).start()
    try:
        host.request("initialize", {})
        session = host.request("session.create", {"workspacePath": str(repo), "title": "Research runtime fixture", "backend": "api"})
        ident = session["info"]["id"]
        accepted = host.request("run.start", {"sessionId": ident, "prompt": "RUNTIME_RESEARCH: inspect the frozen feature history and its file; do not edit.",
            "provider": provider, "options": {"maxIterations": 8, "timeoutSeconds": 90, "maxContextCharacters": 20000,
                "executionMode": "ReadOnly", "research": {"repositoryMemory": True, "repositoryBaseCommit": base,
                    "compressionGuidance": True, "readOnlyPrefetch": True, "maxPrefetches": 4}}})
        deadline = time.monotonic() + 100
        while not any(e["type"] in ("run.completed", "run.failed", "run.cancelled") for e in host.events):
            host.next(max(0.1, deadline - time.monotonic()))
        terminal = next(e for e in reversed(host.events) if e["type"].startswith("run."))
        check(terminal["type"] == "run.completed" and terminal["runId"] == accepted["runId"], "Research runtime failed")
        check(host.approvals == 0, "Read-only research unexpectedly requested writes")
        events = host.request("session.events", {"sessionId": ident})
        kinds = {e["type"] for e in events}
        check({"research.configured", "speculation.started", "speculation.hit", "speculation.summary", "context.observations_compressed"} <= kinds,
              "Research runtime did not exercise every requested option: " + str(sorted(kinds)))
        memory = next(e["result"] for e in events if e["type"] == "tool.completed" and (e.get("call") or {}).get("name") == "repository_memory")
        check(not memory["isError"], "Repository memory tool failed")
        value = json.loads(memory["content"])
        check(value["baseCommit"] == base and value["candidates"], "Repository memory did not use the frozen base")
        check(future not in memory["content"] and future_sha not in memory["content"], "Future Git evidence leaked")
        raw = next(e["result"]["content"] for e in events if e["type"] == "tool.completed" and (e.get("call") or {}).get("id") == "runtime-large")
        check(len(raw) > 2000, "Large raw observation was not retained")
        check(any("compressed-observation" in json.dumps(request) for request in server.requests), "Compressed model view missing")
        write_json(root / "runtime-events.json", events)
        history = host.request("session.load", {"sessionId": ident})
        write_json(root / "runtime-session.json", history)
        check(files_hashes(repo) == before, "Read-only runtime changed fixture sources")
        return {"baseCommit": base, "excludedFutureCommit": future_sha, "events": len(events),
                "speculationHit": True, "rawObservationCharacters": len(raw), "repositoryCandidates": len(value["candidates"])}
    finally:
        try:
            host.stop()
        finally:
            (root / "runtime-stderr.txt").write_text("".join(errors), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", type=Path, default=ROOT / "src/Workbench.Host/bin/Debug/net11.0/Workbench.Host.dll")
    args = parser.parse_args()
    host_path = args.host.resolve()
    check(host_path.is_file(), "Build the Host before running this fixture; this script never builds it.")
    helper = load_helper(host_path)
    root = ROOT / "artifacts" / "verification" / "research" / uuid.uuid4().hex
    root.mkdir(parents=True)
    result = {"passed": False, "mode": "local-sse-real-host-research", "externalModelCalls": 0,
              "managedBinaries": {p.name: digest(p) for p in host_path.parent.glob("Workbench.*.dll")}}
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), FixtureHandler)
    server.requests, server.errors, server.proposals, server.summaries = [], [], [], []
    threading.Thread(target=server.serve_forever, daemon=True).start()
    try:
        repo = root / "source-workspace"
        base = make_repo(repo)
        original = files_hashes(repo)
        server.validator_hash = original["verify.py"]
        provider = {"endpoint": f"http://127.0.0.1:{server.server_port}/v1", "model": "research-fixture",
                    "apiKeyEnvironmentVariable": KEY, "provider": "OpenAiCompatible", "maxOutputTokens": 2000}
        env = dict(os.environ)
        env.pop(KEY, None)
        if helper.DOTNET.exists():
            env["DOTNET_ROOT"] = str(helper.DOTNET.parent)
            env["PATH"] = str(helper.DOTNET.parent) + os.pathsep + env.get("PATH", "")
        common = {"provider": provider, "tasks": tasks(repo, base, "SKILL"), "initialSkill": INITIAL,
                  "taskMaxIterations": 12, "taskContextCharacters": 40000,
                  "options": {"strategy": "SkillOpt", "maxCandidates": 2, "maxProposals": 1, "maxEvaluations": 8,
                    "maxModelCalls": 40, "maxInputTokens": 100000, "maxOutputTokens": 50000,
                    "timeoutSeconds": 180, "operationTimeoutSeconds": 40, "maxCandidateCharacters": 2000,
                    "maxChangedCharacters": 500, "requiredLiterals": [LITERAL]}}
        out = root / "validate-must-not-exist"
        validated = batch(helper, common, root / "validate-skill.json", out, "--optimize-skill", False, env)
        check(validated["valid"] and validated["modelCalls"] == 0 and not validated["executesCommands"]
              and not out.exists() and not server.requests, "Validate-only caused side effects or needed a credential")
        env[KEY] = "fixture-only-not-a-secret"
        result["skillOptimization"] = []
        for strategy in ("Gepa", "SkillOpt"):
            spec = json.loads(json.dumps(common))
            spec["options"]["strategy"] = strategy
            output = root / strategy.lower()
            batch(helper, spec, root / (strategy.lower() + "-spec.json"), output, "--optimize-skill", True, env)
            result["skillOptimization"].append(verify_skill(output, original))
        check(server.proposals == ["Gepa", "SkillOpt"], "Both proposer mechanisms were not exercised")
        compression = {"provider": provider, "tasks": tasks(repo, base, "COMPRESSION"), "taskMaxIterations": 12,
            "taskContextCharacters": 8000, "initialGuidance": {"schemaVersion": 1, "id": "fixture-baseline", "revision": 1,
                "historyInstructions": "Summarize the observation history concisely.",
                "observationInstructions": "Keep paths, hashes, cursors and failures.", "observationCharacterLimit": 1200,
                "anchorCharacterLimit": 200}, "compressionFailures": [{"caseId": "train", "datasetSplit": "development",
                "originalContext": "Requested edit still pending after observations.", "compressedContext": "Read logs.",
                "failure": "The compressed context lost the pending edit."}],
            "compressionBudget": {"maxRequestCharacters": 16000, "maxResponseCharacters": 4000, "maxEvaluations": 4,
                "maxReportedTokens": 100000, "timeoutSeconds": 180}}
        clean_env = dict(env)
        clean_env.pop(KEY)
        before_calls = len(server.requests)
        compression_validate = root / "validate-compression-must-not-exist"
        validated = batch(helper, compression, root / "validate-compression.json", compression_validate,
                          "--optimize-compression", False, clean_env)
        check(validated["valid"] and not compression_validate.exists() and len(server.requests) == before_calls,
              "Compression validate-only executed model or filesystem work")
        compression_output = root / "compression"
        batch(helper, compression, root / "compression-spec.json", compression_output, "--optimize-compression", True, env)
        comparison = json.loads((compression_output / "comparison.json").read_text(encoding="utf-8"))
        held = json.loads((compression_output / "holdout.json").read_text(encoding="utf-8"))
        check(comparison["accepted"] and comparison["selected"]["revision"] == 2, "Measured compression candidate was not selected")
        check(all(not pair["baseline"]["taskSucceeded"] and pair["candidate"]["taskSucceeded"] for pair in comparison["scores"]),
              "Guideline did not change actual frozen task success")
        check(all(not pair["baseline"]["taskSucceeded"] and pair["candidate"]["taskSucceeded"] for pair in held["cases"]),
              "Frozen held-out compression evaluation missing")
        check({(s["revision"], s["retained"]) for s in server.summaries} >= {(1, False), (2, True)},
              "Versioned guidelines never reached actual summary calls")
        result["compression"] = {"accepted": comparison["accepted"], "summaryCalls": len(server.summaries),
                                  "reportedTokens": held["reportedTokens"], "holdoutCases": len(held["cases"])}
        result["runtime"] = verify_runtime(helper, root, provider, server)
        check(files_hashes(repo) == original and not git(repo, "status", "--porcelain"), "Optimization changed its source repository")
        check(not (repo / ".workbench" / "skills").exists() and not (repo / "SKILL.md").exists(), "A candidate was auto-deployed")
        check(not server.errors, "Loopback model fixture failed: " + "\n".join(server.errors))
        result.update(passed=True, sourceCommit=base, sourceUnchanged=True, automaticallyActivated=False,
                      fixtureRequests=len(server.requests), validateOnlyWithoutKey=True)
    except Exception as error:
        result["error"] = type(error).__name__ + ": " + str(error)
        result["traceback"] = traceback.format_exc()
        raise
    finally:
        server.shutdown()
        server.server_close()
        write_json(root / "model-requests.json", server.requests)
        write_json(root / "fixture-errors.json", server.errors)
        write_json(root / "result.json", result)
        print(json.dumps({"passed": result["passed"], "result": str(root / "result.json")}, ensure_ascii=False))


if __name__ == "__main__":
    main()
