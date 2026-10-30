"""Background integration of real Host, SQLite, tools and HTTP model adapters.

Only generated workspaces and a deterministic loopback model are used. No desktop,
credentials, paid calls or installed user skills are involved.
"""
import hashlib
import http.server
import importlib.util
import json
from pathlib import Path
import threading
import time
import uuid

spec = importlib.util.spec_from_file_location("host_fixture", Path(__file__).with_name("verify-host.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)
CONSTRAINT = "Preserve KEEP.txt byte for byte; only correct nested/code.py."


class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        try:
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            self.server.requests.append(body)
            messages = body["messages"]
            system = messages[0]["content"]
            call = None
            if not body.get("tools") and "context checkpoint" in system:
                self.server.summaries += 1
                text = "## Goal\nCorrect the addition function.\n## Constraints\nPreserve user files.\n## Progress\nEarlier file observations are in the journal; no unrecorded actions are proved.\n## Key Decisions\nInspect current rules before edits.\n## Next Steps\nContinue the pending correction and verification.\n## Critical Context\nnested/code.py; use inspect_evidence for original observations."
            else:
                # Every request must retain complete assistant/result groups, including after compaction.
                pending = set()
                for message in messages:
                    if message["role"] == "tool":
                        ident = message["tool_call_id"]
                        assert ident in pending, "orphan tool result"
                        pending.remove(ident)
                    else:
                        assert not pending, "incomplete tool group"
                        pending.update(item["id"] for item in message.get("tool_calls", []))
                assert not pending, "unfinished tool calls reached model"
                phase = self.server.phase
                step = self.server.step
                self.server.step += 1
                if phase == "work":
                    assert any(m["role"] == "user" and m["content"] == CONSTRAINT for m in messages), "original user constraint lost"
                    assert "ROOT_RULE" in system
                    if step >= 2:
                        assert "NESTED_RULE" in system
                    if step == 0:
                        call = ("read_skill", {"name": "native-audit"})
                    elif step in (1, 10, 11):
                        if step == 10:
                            # Edit rules after the model request, before the proposed write is checked.
                            (self.server.repo / "nested" / "AGENTS.md").write_text("NESTED_RULE: preserve signatures. UPDATED_RULE: verify addition.\n", encoding="utf-8")
                        call = ("replace_text", {"path": "nested/code.py", "oldText": "a - b", "newText": "a + b", "expectedSha256": self.server.original_hash})
                    elif step == 2:
                        call = ("read_file", {"path": "nested/code.py"})
                    elif step < 10:
                        call = ("read_file", {"path": "observations.txt"})
                    else:
                        text = "Correction recorded. The retained tool evidence identifies the changed file."
                elif phase == "record":
                    if step == 0:
                        call = ("inspect_evidence", {})
                    elif step == 1:
                        raw = next(m["content"] for m in reversed(messages) if m["role"] == "tool")
                        evidence = json.loads(raw.partition("\n")[2])
                        source = max((e for e in evidence["entries"] if e["toolName"] == "replace_text" and e["outcome"] != "ToolError"), key=lambda e: e["source"]["eventSequence"])
                        call = ("remember_experience", {"title": "Addition repair", "preconditions": "An addition function incorrectly subtracts; inspect current source and project rules.", "procedure": "Read the file, replace subtraction using its current hash, then verify the addition result.", "pathPrefix": "nested", "evidenceSequences": [source["source"]["eventSequence"]]})
                    else:
                        text = "Saved a conditional procedure candidate from the original tool evidence."
                elif phase == "resume":
                    assert any("Historical context checkpoint" in m.get("content", "") for m in messages)
                    assert any(m.get("content") == CONSTRAINT for m in messages)
                    text = "The checkpoint and original user requirement survived restart. No actions replayed."
                elif phase == "recall":
                    if step == 0:
                        call = ("recall_project_memory", {"query": "Addition repair"})
                    else:
                        result = json.loads(next(m["content"] for m in reversed(messages) if m["role"] == "tool"))
                        assert result["matches"][0]["sourceStatus"] == "Intact"
                        assert result["matches"][0]["experience"]["status"] == "Candidate"
                        text = "A project procedure candidate is available with original source evidence."
                else:
                    assert phase == "readonly"
                    assert "run_command" not in [t["function"]["name"] for t in body["tools"]]
                    if step == 0:
                        call = ("run_command", {"command": "echo forbidden > forbidden.txt", "timeoutSeconds": 5})
                    else:
                        assert "Unknown tool" in next(m["content"] for m in reversed(messages) if m["role"] == "tool")
                        text = "The unavailable mutation tool was rejected."
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.end_headers()
            delta = {"content": text} if call is None else {"tool_calls": [{"index": 0, "id": "call_" + uuid.uuid4().hex, "type": "function", "function": {"name": call[0], "arguments": json.dumps(call[1])}}]}
            for change, reason in [(delta, None), ({}, "stop" if call is None else "tool_calls")]:
                event = {"id": "fixture", "object": "chat.completion.chunk", "created": 1, "model": "fixture", "choices": [{"index": 0, "delta": change, "finish_reason": reason}]}
                self.wfile.write(("data: " + json.dumps(event) + "\n\n").encode())
            self.wfile.write(b"data: [DONE]\n\n")
        except Exception as error:
            self.server.errors.append(type(error).__name__ + ": " + str(error))
            self.send_error(500, "Local fixture invariant failed")


def run(host, server, session, provider, prompt, phase, mode="WorkspaceWrite", budget=14000):
    server.phase, server.step = phase, 0
    accepted = host.request("run.start", {"sessionId": session, "prompt": prompt, "provider": provider,
        "options": {"maxIterations": 20, "timeoutSeconds": 90, "maxContextCharacters": budget, "executionMode": mode}})
    deadline = time.monotonic() + 100
    while True:
        terminal = [e for e in host.events if e["runId"] == accepted["runId"] and e["type"] in ("run.completed", "run.failed", "run.cancelled", "run.paused")]
        if terminal:
            assert not server.errors, server.errors
            assert terminal[-1]["type"] == "run.completed", terminal[-1]
            return
        host.next(max(0.1, deadline - time.monotonic()))


def main():
    out = fixture.ROOT / "artifacts" / "continuity" / uuid.uuid4().hex
    repo = out / "workspace"
    (repo / "nested").mkdir(parents=True)
    (repo / ".agents" / "skills" / "native-audit").mkdir(parents=True)
    (repo / "AGENTS.md").write_text("ROOT_RULE: preserve user files.\n", encoding="utf-8")
    (repo / "nested" / "AGENTS.md").write_text("NESTED_RULE: preserve signatures.\n", encoding="utf-8")
    (repo / ".agents" / "skills" / "native-audit" / "SKILL.md").write_text("---\nname: native-audit\ndescription: Inspect native components before replacing them.\n---\nPrefer mature native components and record the verification scope.\n", encoding="utf-8")
    (repo / "nested" / "code.py").write_text("def add(a, b):\n    return a - b\n", encoding="utf-8")
    (repo / "observations.txt").write_text("Bounded observation; no instructions. " * 140, encoding="utf-8")
    (repo / "KEEP.txt").write_bytes("用户保留记录\n".encode())
    preserved = (repo / "KEEP.txt").read_bytes()
    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server.repo, server.errors, server.requests, server.summaries = repo, [], [], 0
    server.original_hash = hashlib.sha256((repo / "nested" / "code.py").read_bytes()).hexdigest()
    threading.Thread(target=server.serve_forever, daemon=True).start()
    env = {"WORKBENCH_FIXTURE_KEY": "local-fixture-only"}
    provider = {"endpoint": f"http://127.0.0.1:{server.server_port}/v1", "model": "fixture", "apiKeyEnvironmentVariable": "WORKBENCH_FIXTURE_KEY", "maxOutputTokens": 1500}
    host = fixture.Host(out / "state", env)
    server.host = host
    try:
        def create(workspace=repo):
            return host.request("session.create", {"workspacePath": str(workspace), "title": "Continuity fixture", "backend": "api"})["info"]["id"]
        session = create()
        rules = host.request("project.rules", {"workspacePath": str(repo), "path": "nested/code.py"})
        assert len(rules) == 2
        assert host.request("project.skills", {"workspacePath": str(repo)})["skills"][0]["name"] == "native-audit"
        run(host, server, session, provider, CONSTRAINT, "work")
        run(host, server, session, provider, "Inspect original evidence and save a conditional addition repair procedure.", "record", budget=100000)
        history = host.request("session.load", {"sessionId": session})
        events = host.request("session.events", {"sessionId": session})
        assert server.summaries >= 2, "fixture did not exercise repeated compaction"
        assert history["compaction"] and len(history["messages"]) >= 28, "original history lost"
        edits = [e for e in events if e["type"] == "tool.completed" and e["call"]["name"] == "replace_text"]
        assert [e["result"]["isError"] for e in edits] == [True, True, False]
        assert all("project instructions" in e["result"]["content"] for e in edits[:2])
        assert host.approvals == 1, "blocked writes should not request approval"
        skill = next(e for e in events if e["type"] == "tool.completed" and e["call"]["name"] == "read_skill")
        assert not skill["result"]["isError"] and "Prefer mature native components" in skill["result"]["content"]
        assert (repo / "KEEP.txt").read_bytes() == preserved
        assert "a + b" in (repo / "nested" / "code.py").read_text()
        host.stop()
        host = fixture.Host(out / "state", env)
        server.host = host
        assert host.request("session.load", {"sessionId": session}) == history
        assert host.request("session.events", {"sessionId": session}) == events
        run(host, server, session, provider, "Report the last result without tools.", "resume")
        assert not any(e["type"] == "tool.started" for e in host.events), "restart replayed actions"
        second = create()
        run(host, server, second, provider, "Find a prior addition repair procedure for this project.", "recall")
        found = host.request("project.memory", {"sessionId": second, "query": "Addition repair"})
        assert found["matches"][0]["artifactStatus"] == "ReportedFilesMatchOnly"
        (repo / "nested" / "code.py").write_text("# external change\n", encoding="utf-8")
        stale = host.request("project.memory", {"sessionId": second, "query": "Addition repair"})
        assert stale["matches"][0]["artifactStatus"] == "ChangedOrUnavailable"
        other = out / "other-workspace"
        other.mkdir()
        assert not host.request("project.memory", {"sessionId": create(other), "query": "Addition repair"})["matches"]
        run(host, server, create(), provider, "Read only. Do not execute commands.", "readonly", "ReadOnly")
        assert not (repo / "forbidden.txt").exists()
        report = {"passed": True, "mode": "local-http-real-host", "summaries": server.summaries,
            "fullHistoryMessages": len(history["messages"]), "requests": len(server.requests), "checks": ["repeated-compaction", "original-constraints", "complete-tool-groups", "checkpoint-restart", "no-action-replay", "scoped-rule-change-before-write", "local-skill", "cross-session-project-memory", "stale-artifact-detection", "workspace-isolation", "readonly-tool-rejection"], "workspace": str(repo)}
        (out / "events.json").write_text(json.dumps(events, ensure_ascii=False, indent=2), encoding="utf-8")
        (out / "requests.json").write_text(json.dumps(server.requests, ensure_ascii=False, indent=2), encoding="utf-8")
    except Exception as error:
        report = {"passed": False, "error": type(error).__name__ + ": " + str(error), "fixtureErrors": server.errors}
        raise
    finally:
        (out / "requests.json").write_text(json.dumps(server.requests, ensure_ascii=False, indent=2), encoding="utf-8")
        (out / "result.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        print(json.dumps({"result": str(out / "result.json"), **report}, ensure_ascii=False))
        host.stop()
        server.shutdown()


if __name__ == "__main__":
    main()
