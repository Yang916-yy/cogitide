"""Probe an explicitly selected preinstalled Linux Host through real WSL/system SSH.

No SDK, GUI, model request, account lookup, credential copy or installation is run.
The RPC request/event helper is reused from verify-host.py. --loopback-ssh adds an
optional AsyncSSH fixture server; the production client remains system OpenSSH.
"""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PureWindowsPath
import queue
import re
import shlex
import subprocess
import sys
import threading
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("remote_host_protocol_fixture", Path(__file__).with_name("verify-host.py"))
fixture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fixture)

SSH_OPTIONS = ["-T", "-x", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes",
               "-o", "ForwardAgent=no", "-o", "ClearAllForwardings=yes", "-o", "PermitLocalCommand=no",
               "-o", "RemoteCommand=none", "-o", "StdinNull=no", "-o", "ForkAfterAuthentication=no",
               "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=2"]
REQUIRED_CAPABILITIES = {"session.view", "run.continue", "workspace.files.page", "workspace.read.page"}
CONTENTS = {"probe 'quoted' 中文.txt": "Cogitide real remote fixture.\nUnicode 中文 😀 stays intact.\nThird line.\n",
            "notes.txt": "Generated fixture only; no personal workspace or account is read.\n",
            "nested/extra.txt": "A second inventory page must remain reachable.\n"}


class ProbeFailure(RuntimeError):
    pass


class ProbeRequestFailure(ProbeFailure):
    pass


def require(condition, message):
    if not condition:
        raise ProbeFailure(message)


def absolute_posix(value):
    if not value or not value.startswith("/") or len(value) > 4096 or any(ord(c) < 32 or ord(c) == 127 for c in value):
        raise argparse.ArgumentTypeError("Use an absolute POSIX path without control characters.")
    return value.rstrip("/") or "/"


def wsl_path(path):
    value = PureWindowsPath(str(path.resolve()))
    require(len(value.drive) == 2 and value.drive[1] == ":", "WSL fixture files need a local drive path and the default /mnt/<drive> mount.")
    return "/mnt/" + value.drive[0].lower() + "/" + "/".join(value.parts[1:])


def host_arguments(args, data_directory):
    command = [args.dotnet_posix] if args.host_posix.lower().endswith(".dll") else []
    return command + [args.host_posix, "--stdio", "--data-dir", data_directory]


def launch_arguments(args, data_directory, ssh_config=None):
    command = host_arguments(args, data_directory)
    if args.wsl and not args.loopback_ssh:
        return ["wsl.exe", "--distribution", args.wsl, "--exec"] + command
    destination = "cogitide-fixture" if args.loopback_ssh else args.ssh
    config = ["-F", str(ssh_config)] if ssh_config else []
    return ["ssh"] + config + SSH_OPTIONS + ["--", destination, "exec " + shlex.join(command)]


class RemoteHost(fixture.Host):
    """Only process startup/diagnostic bounds differ from the existing RPC fixture."""
    def __init__(self, command):
        self.process = subprocess.Popen(command, cwd=ROOT, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="strict",
                                        creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        self.messages = queue.Queue(maxsize=256)
        self.events, self.reply_cache, self.pending_approvals = [], {}, []
        self.number = self.approvals = 0
        self.auto_approve = False
        self.hints = set()
        self.read_failure = None
        self._stopped = False

        def read():
            try:
                while True:
                    line = self.process.stdout.readline(1_048_577)
                    if not line:
                        break
                    if len(line) > 1_048_576:
                        raise ProbeFailure("Protocol frame exceeded this fixture's 1 MiB bound.")
                    message = json.loads(line)
                    if not isinstance(message, dict):
                        raise ProbeFailure("Host returned a non-object protocol frame.")
                    self.messages.put(message, timeout=1)
            except (ValueError, OSError, queue.Full, ProbeFailure):
                self.read_failure = "Remote protocol output was invalid or exceeded fixture bounds."
            finally:
                try:
                    self.messages.put({"closed": True}, timeout=1)
                except queue.Full:
                    self.read_failure = "Remote protocol queue exceeded fixture bounds."

        def diagnostics():
            remaining, tail = 16_384, ""
            categories = {"permission denied": "SSH authentication failed.", "host key verification failed": "SSH host-key verification failed.",
                          "remote host identification has changed": "SSH fixture host-key mismatch was rejected.",
                          "no such file": "The preinstalled Host path was not found.", "not found": "The Host/runtime/distribution was not found.",
                          "connection refused": "SSH refused the loopback or explicitly selected endpoint."}
            try:
                while True:
                    chunk = self.process.stderr.read(1024)
                    if not chunk:
                        break
                    if remaining <= 0:
                        continue
                    sample = (tail + chunk[:remaining]).lower()
                    remaining -= len(chunk)
                    for pattern, hint in categories.items():
                        if pattern in sample:
                            self.hints.add(hint)
                    tail = sample[-128:]
            except (ValueError, OSError):
                pass

        self._readers = [threading.Thread(target=read, daemon=True), threading.Thread(target=diagnostics, daemon=True)]
        for reader in self._readers:
            reader.start()

    def request(self, method, params, timeout=25):
        try:
            return super().request(method, params, timeout=timeout)
        except queue.Empty as error:
            raise ProbeFailure("A remote protocol request exceeded its deadline.") from error
        except (OSError, RuntimeError) as error:
            raise ProbeRequestFailure("The remote Host could not complete a protocol request.") from error

    def stop(self, strict=True):
        if self._stopped:
            return
        self._stopped = True
        forced = False
        try:
            self.process.stdin.close()
        except (OSError, ValueError):
            pass
        try:
            self.process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            forced = True
            self.process.kill()
            self.process.wait(timeout=5)
        for reader in self._readers:
            reader.join(timeout=1)
        self.process.stdout.close()
        self.process.stderr.close()
        if strict:
            require(not forced, "The Host did not exit on stdin EOF; owned remote cleanup is unverified.")
            require(self.process.returncode == 0, "The Host/transport exited with a nonzero status.")


def tool_json(host, method, params):
    result = host.request(method, params)
    require(isinstance(result, dict) and not result.get("isError"), "The remote file/command fixture returned an error result.")
    try:
        return json.loads(result["content"])
    except (KeyError, TypeError, ValueError) as error:
        raise ProbeFailure("The remote tool returned an invalid JSON result.") from error


def initialize(host):
    result = host.request("initialize", {"protocolVersion": 1})
    require(result.get("protocolVersion") == 1, "The installed Host wire version does not match this fixture.")
    require(result.get("platform") == "linux", "This remote fixture currently requires a Linux Host.")
    require(REQUIRED_CAPABILITIES.issubset(result.get("capabilities", [])), "The installed Host is missing current desktop capabilities.")


def read_file(host, workspace, path):
    offset, character, digest, parts = 0, 0, None, []
    for _ in range(64):
        params = {"workspacePath": workspace, "path": path, "offset": offset, "characterOffset": character, "limit": 1}
        if digest:
            params["expectedSha256"] = digest
        page = tool_json(host, "workspace.read.page", params)
        digest = digest or page["sha256"]
        require(page["sha256"] == digest, "Remote file changed between snapshot-bound pages.")
        parts.append(page["content"])
        if page.get("nextOffset") is None:
            content = "".join(parts)
            require(hashlib.sha256(content.encode("utf-8")).hexdigest() == digest, "Remote paged content did not match its full-file SHA-256.")
            return content
        offset, character = page["nextOffset"], page.get("nextCharacterOffset", 0)
    raise ProbeFailure("Remote file paging failed to make bounded progress.")


def command(host, workspace, script):
    result = tool_json(host, "workspace.command", {"workspacePath": workspace, "command": script, "timeoutSeconds": 15})
    require(result.get("exitCode") == 0 and not result.get("timedOut"), "A generated fixture command did not complete successfully.")
    return result["output"]


def exercise(args, run_directory, remote_run, ssh_config=None):
    workspace, state = remote_run + "/workspace 'quoted' 中文", remote_run + "/state 'quoted'"
    host = RemoteHost(launch_arguments(args, state, ssh_config))
    checks, session_id = [], None
    try:
        initialize(host)
        checks.append("real Linux Host initialization and required capabilities")
        # The Host created state and its parent in this unique generated fixture root.
        create = ["mkdir -p -- " + shlex.quote(workspace + "/nested")]
        create += ["printf '%s' " + shlex.quote(content) + " > " + shlex.quote(workspace + "/" + name) for name, content in CONTENTS.items()]
        command(host, remote_run, "; ".join(create))
        # artifacts/ is correctly ignored by the enclosing source repository. Give this
        # generated workspace its own index so Git inventory tests its own files rather
        # than inheriting the parent's artifact exclusion. No commit/identity is needed.
        command(host, remote_run, "git -C " + shlex.quote(workspace) + " init -q")
        command(host, remote_run, "git -C " + shlex.quote(workspace) + " add -f -- " + " ".join(shlex.quote(name) for name in CONTENTS))
        validation = host.request("workspace.validate", {"workspacePath": workspace})
        require(validation.get("workspacePath") == workspace, "Workspace validation changed the endpoint-local POSIX path.")
        found, offset, snapshot = [], 0, None
        for _ in range(16):
            query = {"workspacePath": workspace, "offset": offset, "limit": 1}
            if snapshot:
                query["expectedSnapshot"] = snapshot
            page = tool_json(host, "workspace.files.page", query)
            snapshot = snapshot or page["snapshot"]
            require(page["snapshot"] == snapshot, "Inventory snapshot changed between pages.")
            found.extend(page["files"])
            if page.get("nextOffset") is None:
                break
            require(page["nextOffset"] > offset, "Inventory cursor did not advance.")
            offset = page["nextOffset"]
        require(set(found) == set(CONTENTS) and len(found) == len(CONTENTS), "A generated file was missing, duplicated or unreachable through inventory pages.")
        for name, content in CONTENTS.items():
            require(read_file(host, workspace, name) == content, "Remote file read changed generated UTF-8 fixture content.")
        checks.append("POSIX paths with spaces/apostrophes/Unicode, inventory pages and full-file read hashes")
        session = host.request("session.create", {"workspacePath": workspace, "title": "Generated real remote probe", "backend": "api"})
        session_id = session["info"]["id"]
        require("REMOTE_PROBE_OK" in command(host, workspace, "printf '%s\\n' REMOTE_PROBE_OK"), "An explicit generated command produced no observed output.")
        checks.append("explicit generated command output without a model call")
        # Observe the first write before closing Host stdin while a child is still running.
        started = time.monotonic()
        host.send("workspace.command", {"workspacePath": workspace,
                  "command": "printf 'started\\n' > owned-command.txt; sleep 4; printf 'must-not-run\\n' >> owned-command.txt", "timeoutSeconds": 15})
        while True:
            try:
                result = host.request("workspace.read.page", {"workspacePath": workspace, "path": "owned-command.txt", "limit": 10})
            except ProbeRequestFailure:
                result = {"isError": True}
            if not result.get("isError"):
                require(json.loads(result["content"])["content"] == "started\n", "The controlled command completed before EOF was exercised.")
                break
            require(time.monotonic() - started < 3, "The controlled child command did not start within its fixture deadline.")
            time.sleep(0.05)
        host.stop()
        # Wait beyond the delayed write's original deadline before reconnecting and inspecting it.
        time.sleep(max(0, started + 4.5 - time.monotonic()))
        host = RemoteHost(launch_arguments(args, state, ssh_config))
        initialize(host)
        view = host.request("session.view", {"sessionId": session_id, "limit": 5})
        require(view["info"]["workspacePath"] == workspace and view["messages"] == [], "Isolated session persistence changed after reconnect.")
        require(any(item["id"] == session_id for item in host.request("session.list", {})), "The saved isolated session was missing after restart.")
        require(read_file(host, workspace, "owned-command.txt") == "started\n", "The owned child outlived Host EOF or its unobserved command was replayed.")
        require("FRESH_COMMAND_OK" in command(host, workspace, "printf '%s\\n' FRESH_COMMAND_OK"), "A fresh command could not run after reconnect.")
        checks += ["stdin EOF stops the owned child before its delayed write", "isolated session persistence and reconnect without command replay"]
        host.stop()
        return {"checks": checks, "sessionId": session_id, "workspacePath": workspace, "dataDirectory": state,
                "hostClientTestEnvironment": {"WORKBENCH_REMOTE_REAL_PROBE": "1", "WORKBENCH_REMOTE_REAL_KIND": "Ssh" if args.ssh or args.loopback_ssh else "Wsl",
                    "WORKBENCH_REMOTE_REAL_DESTINATION": "cogitide-fixture" if args.loopback_ssh else args.ssh or args.wsl,
                    "WORKBENCH_REMOTE_REAL_HOST_PATH": args.host_posix, "WORKBENCH_REMOTE_REAL_DOTNET_PATH": args.dotnet_posix,
                    "WORKBENCH_REMOTE_REAL_WORKSPACE": workspace, "WORKBENCH_REMOTE_REAL_DATA_DIRECTORY": remote_run + "/adapter-state",
                    **({"WORKBENCH_REMOTE_TEST_SSH_CONFIG": str(ssh_config)} if ssh_config else {})}}
    except Exception:
        host.stop(strict=False)
        raise


def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--wsl", metavar="DISTRIBUTION", help="Explicit installed WSL distribution; Windows only.")
    mode.add_argument("--ssh", metavar="ALIAS", help="Explicit user-authorized SSH hostname/configuration alias.")
    parser.add_argument("--host-posix", type=absolute_posix, required=True, help="Preinstalled matching Linux Host executable or DLL.")
    parser.add_argument("--dotnet-posix", default="dotnet", help="Remote dotnet executable for a DLL; no runtime is installed.")
    parser.add_argument("--remote-root", type=absolute_posix, help="Existing parent directory for --ssh; a unique generated child is used.")
    parser.add_argument("--loopback-ssh", action="store_true", help="With --wsl, use an isolated optional AsyncSSH loopback server and actual system ssh.")
    parser.add_argument("--hold-for-client-seconds", type=int, default=0, help="Keep the loopback fixture alive after reporting opt-in HostClient test settings (0-300).")
    args = parser.parse_args()
    if args.wsl and os.name != "nt":
        parser.error("--wsl requires Windows.")
    if args.wsl and (not args.wsl.strip() or args.wsl.startswith("-") or len(args.wsl) > 255 or any(ord(c) < 32 or 127 <= ord(c) <= 159 for c in args.wsl)):
        parser.error("--wsl needs an installed distribution name without options or control characters.")
    if args.ssh and (not re.fullmatch(r"[A-Za-z0-9_.@:\[\]-]+", args.ssh) or args.ssh.startswith("-")):
        parser.error("--ssh needs a hostname/configuration alias without SSH options or a URL.")
    if args.ssh and not args.remote_root:
        parser.error("--ssh requires an explicit --remote-root for generated fixture files.")
    if args.loopback_ssh and not args.wsl:
        parser.error("--loopback-ssh requires --wsl as its isolated Linux Host backend.")
    if not 0 <= args.hold_for_client_seconds <= 300 or (args.hold_for_client_seconds and not args.loopback_ssh):
        parser.error("--hold-for-client-seconds is 0-300 and requires --loopback-ssh.")
    if not args.dotnet_posix.startswith("/") and not re.fullmatch(r"[A-Za-z0-9_.-]+", args.dotnet_posix):
        parser.error("--dotnet-posix needs an absolute POSIX path or one executable name.")
    if args.dotnet_posix.startswith("-") or any(ord(c) < 32 for c in args.dotnet_posix):
        parser.error("--dotnet-posix cannot contain options or control characters.")
    return args


def main():
    args = parse_args()
    run_directory = ROOT / "artifacts" / "remote-checks" / ("cogitide-remote-check-" + uuid.uuid4().hex)
    run_directory.mkdir(parents=True)
    remote_run = wsl_path(run_directory) if args.wsl else args.remote_root.rstrip("/") + "/" + run_directory.name
    report = {"passed": False, "transport": "loopback-system-ssh-to-wsl" if args.loopback_ssh else "wsl" if args.wsl else "ssh",
              "modelCalls": 0, "scope": "Real Linux Host transport/protocol fixture; actual Desktop HostClient is a separate opt-in xUnit test.",
              "limitations": ["No GUI acceptance or public remote server compatibility claim.", "No account, model, external MCP or paid-provider path is exercised."]}
    server = None
    try:
        if args.loopback_ssh:
            helper_spec = importlib.util.spec_from_file_location("remote_loopback_fixture", Path(__file__).with_name("remote-loopback-fixture.py"))
            helper = importlib.util.module_from_spec(helper_spec)
            helper_spec.loader.exec_module(helper)
            server = helper.LoopbackSshFixture(args.wsl, args.host_posix, args.dotnet_posix, remote_run)
            server.start()
        report.update(exercise(args, run_directory, remote_run, server.config_path if server else None))
        if server:
            for index, config in enumerate(server.rejected_configs):
                rejected = RemoteHost(launch_arguments(args, remote_run + "/rejected-state", config))
                accepted = False
                try:
                    initialize(rejected)
                    accepted = True
                except ProbeFailure:
                    pass
                finally:
                    rejected.stop(strict=False)
                require(not accepted and rejected.process.returncode != 0, "An invalid generated SSH key or host pin was accepted.")
                expected = "host-key" if index == 0 else "authentication"
                require(any(expected in hint.lower() for hint in rejected.hints), "SSH rejection did not report the expected redacted host-key/authentication category.")
            report["checks"] += ["actual OpenSSH rejects a mismatched generated host pin", "actual OpenSSH rejects a wrong generated client key"]
            report["loopbackServer"] = {"implementation": "AsyncSSH", "version": server.version, "address": "127.0.0.1", "port": server.port,
                                        "credentialStorage": "ephemeral restricted temporary directory outside the repository"}
        report["passed"] = True
    except Exception as error:
        report["failure"] = str(error) if isinstance(error, ProbeFailure) else "Remote fixture failed (" + type(error).__name__ + "); no raw backend diagnostics were persisted."
    finally:
        result_path = run_directory / "result.json"
        if server:
            report["loopbackFixtureActive"] = True
        result_path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
        if server:
            if report["passed"] and args.hold_for_client_seconds:
                # Root may run the separately opted-in HostClient fixture while this bounded server is live.
                print(json.dumps({"fixtureReady": True, "resultPath": str(result_path), "hostClientTestEnvironment": report["hostClientTestEnvironment"]}, ensure_ascii=False), flush=True)
                time.sleep(args.hold_for_client_seconds)
            server.stop()
            report["loopbackFixtureActive"] = False
            report["loopbackCleanupVerified"] = server.failure is None
            if server.failure:
                report["passed"] = False
                report["failure"] = "The isolated loopback fixture could not verify complete shutdown."
            result_path.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
        print(json.dumps({**report, "resultPath": str(result_path)}, ensure_ascii=False), flush=True)
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
