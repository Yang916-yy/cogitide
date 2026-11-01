"""Optional test-only AsyncSSH server: real OpenSSH client -> preinstalled WSL Host.

Uses AsyncSSH's documented server/process redirection APIs, not an SSH protocol
implementation. Keys/config/known_hosts are ephemeral and never touch ~/.ssh.
"""
import asyncio
import getpass
import json
import os
from pathlib import Path
import posixpath
import re
import shlex
import subprocess
import tempfile
import threading

import asyncssh


class LoopbackSshFixture:
    def __init__(self, distribution, host_path, dotnet_path, allowed_root):
        if asyncssh.__version__ != "2.24.1":
            raise RuntimeError("The optional loopback fixture requires asyncssh==2.24.1.")
        self.version = asyncssh.__version__
        self.distribution, self.host_path, self.dotnet_path = distribution, host_path, dotnet_path
        self.allowed_root = posixpath.normpath(allowed_root)
        fixture_name = posixpath.basename(self.allowed_root)
        if not re.fullmatch(r"cogitide-remote-check-[0-9a-f]{32}", fixture_name):
            raise ValueError("The loopback fixture requires its generated GUID root.")
        self.native_service_root = "/tmp/" + fixture_name
        self._temporary = tempfile.TemporaryDirectory(prefix="cogitide-ssh-fixture-")
        self.directory = Path(self._temporary.name)
        self._principal = None
        if os.name == "nt":
            domain = os.environ.get("USERDOMAIN", "")
            self._principal = (domain + "\\" if domain else "") + getpass.getuser()
            subprocess.run(["icacls", str(self.directory), "/inheritance:r", "/grant:r", self._principal + ":(OI)(CI)F"],
                           check=True, capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
        else:
            self.directory.chmod(0o700)
        self._client_key = asyncssh.generate_private_key("ssh-ed25519")
        self._wrong_client_key = asyncssh.generate_private_key("ssh-ed25519")
        self._host_key = asyncssh.generate_private_key("ssh-ed25519")
        self._wrong_host_key = asyncssh.generate_private_key("ssh-ed25519")
        self._client_key.write_private_key(self.directory / "client_key")
        self._wrong_client_key.write_private_key(self.directory / "wrong_client_key")
        for name in ("client_key", "wrong_client_key"):
            key_path = self.directory / name
            if os.name == "nt":
                # Python's secure-file creation can add an explicit OWNER RIGHTS ACE.
                # OpenSSH checks each private key's ACL, so directory permissions alone
                # are insufficient. Restrict only these two generated ephemeral files.
                if key_path.is_symlink() or key_path.resolve().parent != self.directory.resolve():
                    raise RuntimeError("The generated fixture private key escaped its temporary directory.")
                subprocess.run(["icacls", str(key_path), "/inheritance:r", "/grant:r", self._principal + ":F", "/remove:g", "*S-1-3-4"],
                               check=True, capture_output=True, creationflags=subprocess.CREATE_NO_WINDOW)
            else:
                key_path.chmod(0o600)
        self.config_path = self.directory / "ssh_config"
        self.rejected_configs = [self.directory / "ssh_config_wrong_pin", self.directory / "ssh_config_wrong_client"]
        self._ready = threading.Event()
        self._thread = None
        self._loop = self._stop_event = None
        self._connections, self._children = set(), set()
        self._service_directories = set()
        self.failure = None
        self.port = None

    def _write_config(self, path, identity, known_hosts):
        # Dedicated -F configuration prevents access to personal aliases, keys or known_hosts.
        def quoted(value):
            return '"' + str(value).replace("\\", "/").replace('"', '\\"') + '"'
        null_file = "NUL" if os.name == "nt" else "/dev/null"
        path.write_text("\n".join([
            "Host cogitide-fixture", "  HostName 127.0.0.1", "  Port " + str(self.port), "  User cogitide-fixture",
            "  IdentityFile " + quoted(identity), "  IdentitiesOnly yes", "  IdentityAgent none",
            "  UserKnownHostsFile " + quoted(known_hosts), "  GlobalKnownHostsFile " + null_file,
            "  StrictHostKeyChecking yes", "  UpdateHostKeys no", "  BatchMode yes",
            "  ProxyCommand none", "  ProxyJump none", "  ControlMaster no", "  ControlPath none",
            "  CanonicalizeHostname no", "  LogLevel ERROR", ""
        ]), encoding="utf-8")

    def start(self):
        # WSL startup is a bounded native prerequisite, not part of the SSH listener's
        # thread/event-loop readiness window. Keep stdin detached just as for other fixture setup.
        self._prepare_native_service_root()
        def run():
            try:
                asyncio.run(self._serve())
            except Exception as error:
                self.failure = type(error).__name__
                print(json.dumps({"loopbackFixtureDiagnostic": "SshListenerStartup" + type(error).__name__}), flush=True)
                self._ready.set()
        self._thread = threading.Thread(target=run, daemon=True)
        self._thread.start()
        if not self._ready.wait(timeout=15) or self.failure:
            self.stop()
            raise RuntimeError("The isolated loopback SSH fixture could not start.")

    def _prepare_native_service_root(self):
        # DrvFS commonly reports 0777 for /mnt/<drive> paths. Service tokens require real
        # owner-only POSIX permissions, so only service data uses this matching native GUID root.
        path = shlex.quote(self.native_service_root)
        script = ("umask 077; if test ! -e " + path + " && test ! -L " + path + "; then mkdir -- " + path + "; fi; "
                  "test -d " + path + " && test ! -L " + path + " && test -O " + path +
                  " && test \"$(stat -c '%a' " + path + ")\" = 700")
        try:
            parent = subprocess.run(["wsl.exe", "--distribution", self.distribution, "--exec", "/bin/sh", "-c", script],
                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=20,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        except (OSError, subprocess.TimeoutExpired) as error:
            category = "NativeServiceParentTimeout" if isinstance(error, subprocess.TimeoutExpired) else "NativeServiceParentLaunchFailed"
            print(json.dumps({"loopbackFixtureDiagnostic": category}), flush=True)
            raise RuntimeError("The generated Linux-native service prerequisite failed.") from None
        if parent.returncode:
            print(json.dumps({"loopbackFixtureDiagnostic": "NativeServiceParentPermissionRejected"}), flush=True)
            raise RuntimeError("The generated Linux-native service parent is not private or owned.")

    async def _serve(self):
        owner = self
        class FixtureServer(asyncssh.SSHServer):
            def connection_made(self, connection):
                self.connection = connection
                owner._connections.add(connection)

            def connection_lost(self, error):
                owner._connections.discard(self.connection)

            def begin_auth(self, username):
                return True

            def public_key_auth_supported(self):
                return True

            def validate_public_key(self, username, key):
                return username == "cogitide-fixture" and key.export_public_key() == owner._client_key.export_public_key()

        self._loop = asyncio.get_running_loop()
        self._stop_event = asyncio.Event()
        listener = await asyncssh.listen("127.0.0.1", 0, server_factory=FixtureServer,
                                        server_host_keys=[self._host_key], process_factory=self._handle_process,
                                        encoding=None, login_timeout=10)
        self.port = listener.get_port()
        (self.directory / "known_hosts").write_bytes(("[127.0.0.1]:" + str(self.port) + " ").encode("ascii") + self._host_key.export_public_key())
        (self.directory / "wrong_known_hosts").write_bytes(("[127.0.0.1]:" + str(self.port) + " ").encode("ascii") + self._wrong_host_key.export_public_key())
        self._write_config(self.config_path, self.directory / "client_key", self.directory / "known_hosts")
        self._write_config(self.rejected_configs[0], self.directory / "client_key", self.directory / "wrong_known_hosts")
        self._write_config(self.rejected_configs[1], self.directory / "wrong_client_key", self.directory / "known_hosts")
        self._ready.set()
        try:
            await self._stop_event.wait()
        finally:
            listener.close()
            await listener.wait_closed()
            connections = list(self._connections)
            for connection in connections:
                connection.close()
            await asyncio.gather(*(connection.wait_closed() for connection in connections), return_exceptions=True)
            for child in list(self._children):
                await self._stop_child(child)
            # A daemon intentionally survives channel EOF. Only this explicit fixture teardown
            # stops daemons launched inside its generated root; no personal data path is accepted.
            for directory in self._service_directories:
                await self._stop_fixture_service(directory)

    async def _stop_fixture_service(self, directory):
        try:
            read = await asyncio.create_subprocess_exec("wsl.exe", "--distribution", self.distribution, "--exec",
                "cat", directory + "/remote-service/identity", stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.DEVNULL,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            identity, _ = await asyncio.wait_for(read.communicate(), timeout=10)
            identity = identity.decode("ascii").strip()
            if read.returncode or not re.fullmatch(r"[0-9a-f]{32}", identity):
                return
            prefix = [self.dotnet_path, self.host_path] if self.host_path.lower().endswith(".dll") else [self.host_path]
            stop = await asyncio.create_subprocess_exec("wsl.exe", "--distribution", self.distribution, "--exec", *prefix,
                "--service-stop", "--data-dir", directory, "--expected-service-id", identity,
                stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            await asyncio.wait_for(stop.wait(), timeout=30)
            absent = await asyncio.create_subprocess_exec("wsl.exe", "--distribution", self.distribution, "--exec",
                "test", "!", "-e", directory + "/remote-service/endpoint.json",
                stdout=asyncio.subprocess.DEVNULL, stderr=asyncio.subprocess.DEVNULL,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            await asyncio.wait_for(absent.wait(), timeout=10)
            if absent.returncode:
                self.failure = "UnverifiedDetachedFixtureCleanup"
        except (OSError, UnicodeError, asyncio.TimeoutError):
            self.failure = "UnverifiedDetachedFixtureCleanup"

    async def _stop_child(self, child):
        if child.returncode is None:
            child.stdin.close()
            try:
                await asyncio.wait_for(child.wait(), timeout=15)
            except asyncio.TimeoutError:
                self.failure = "UnverifiedChildEofCleanup"
                child.kill()
                await child.wait()

    async def _handle_process(self, process):
        child = None
        try:
            # The fixture accepts only the adapter's exact Host argv shape. It never executes
            # a command shell, handles SCP/SFTP, or accepts arbitrary incoming programs.
            text = process.command.decode("utf-8") if isinstance(process.command, bytes) else process.command
            arguments = shlex.split(text or "")
            prefix = [self.dotnet_path, self.host_path] if self.host_path.lower().endswith(".dll") else [self.host_path]
            valid = bool(arguments) and arguments[0] == "exec" and arguments[1:1 + len(prefix)] == prefix
            tail = arguments[1 + len(prefix):] if valid else []
            directory, mode = "", tail[0] if tail else ""
            if len(tail) == 3 and mode in ("--stdio", "--service-start") and tail[1] == "--data-dir":
                directory = posixpath.normpath(tail[2])
            elif len(tail) == 5 and mode == "--service-bridge" and tail[1] == "--expected-service-id" and re.fullmatch(r"[0-9a-f]{32}", tail[2]) and tail[3] == "--data-dir":
                directory = posixpath.normpath(tail[4])
            elif len(tail) == 5 and mode == "--service-stop" and tail[1] == "--data-dir" and tail[3] == "--expected-service-id" and re.fullmatch(r"[0-9a-f]{32}", tail[4]):
                directory = posixpath.normpath(tail[2])
            permitted_root = self.allowed_root if mode == "--stdio" else self.native_service_root
            valid = valid and directory.startswith(permitted_root + "/") and not any(ord(c) < 32 for c in directory)
            if not valid:
                process.stderr.write(b"Fixture rejected unexpected Host launch arguments.\n")
                process.exit(126)
                return
            if mode == "--service-start":
                self._service_directories.add(directory)
            child = await asyncio.create_subprocess_exec("wsl.exe", "--distribution", self.distribution, "--exec", *arguments[1:],
                stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            self._children.add(child)
            # AsyncSSH owns stream forwarding/EOF behavior. This is its documented process
            # redirection API; no SSH packet handling or custom byte pump is implemented.
            await process.redirect(stdin=child.stdin, stdout=child.stdout, stderr=child.stderr)
            status = await child.wait()
            await process.stdout.drain()
            process.exit(status if status >= 0 else 128 - status)
        except Exception as error:
            self.failure = "FixtureProcess" + type(error).__name__
            process.exit(1)
        finally:
            try:
                if child is not None:
                    await self._stop_child(child)
                    self._children.discard(child)
            finally:
                # AsyncSSH registers stream-redirection cleanup awaitables on the
                # process itself. Connection/child exit alone does not await them.
                await process.wait_closed()

    def stop(self):
        if self._loop and self._stop_event and self._loop.is_running():
            self._loop.call_soon_threadsafe(self._stop_event.set)
        if self._thread:
            self._thread.join(timeout=20)
            if self._thread.is_alive():
                self.failure = "LoopbackShutdownTimeout"
        self._temporary.cleanup()
