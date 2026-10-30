"""Check portable packages and optionally exercise a matching native Host without model calls."""
import argparse
import hashlib
import json
import os
import platform
from pathlib import Path
import queue
import re
import struct
import subprocess
import threading
import uuid

ROOT = Path(__file__).resolve().parents[1]


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def native_architecture(path):
    with path.open("rb") as stream:
        header = stream.read(64)
        if header[:2] == b"MZ":
            stream.seek(struct.unpack_from("<I", header, 0x3C)[0])
            pe = stream.read(6)
            assert pe[:4] == b"PE\0\0", "Invalid PE header"
            machine = struct.unpack_from("<H", pe, 4)[0]
            return {0x8664: "win-x64", 0xAA64: "win-arm64"}[machine]
        if header[:4] == b"\x7fELF":
            assert header[4:6] == b"\x02\x01", "Expected a little-endian 64-bit ELF"
            return {62: "linux-x64", 183: "linux-arm64"}[struct.unpack_from("<H", header, 18)[0]]
        if header[:4] == b"\xcf\xfa\xed\xfe":
            return {0x1000007: "osx-x64", 0x100000C: "osx-arm64"}[struct.unpack_from("<I", header, 4)[0]]
    raise AssertionError("Unsupported native application header: " + path.name)


def verify_package(directory, require_source_locks=False, require_unified_kernel=False, require_tool_upgrade=False, require_research=False):
    package = directory.resolve(strict=True)
    assert not (package / ".publish-incomplete").exists(), "Publish did not complete"
    info = read_json(package / "publish-info.json")
    runtime = info["runtime"]
    assert re.fullmatch(r"(win|linux|osx)-(x64|arm64)", runtime), "Unsupported target runtime"
    entries = {}
    for line in (package / "SHA256SUMS").read_text(encoding="utf-8-sig").splitlines():
        checksum, relative = line.split("  ", 1)
        assert re.fullmatch(r"[0-9a-f]{64}", checksum), "Invalid checksum entry"
        path = (package / relative).resolve(strict=True)
        assert path.is_relative_to(package) and path.is_file(), "Checksum path leaves the package"
        assert relative not in entries, "Duplicate checksum path"
        assert digest(path) == checksum, "Checksum mismatch: " + relative
        entries[relative] = checksum
    actual = {path.relative_to(package).as_posix() for path in package.rglob("*") if path.is_file() and path != package / "SHA256SUMS"}
    assert actual == set(entries), "Checksum manifest omits files or lists unexpected paths"
    versions = []
    suffix = ".exe" if runtime.startswith("win-") else ""
    for name in ("Workbench.Desktop", "Workbench.Host"):
        assert native_architecture(package / (name + suffix)) == runtime, "Native executable architecture mismatch"
        assert (package / (name + ".dll")).is_file(), "Managed application missing"
        assert read_json(package / (name + ".deps.json"))["runtimeTarget"]["name"].endswith("/" + runtime), "Dependency runtime mismatch"
        options = read_json(package / (name + ".runtimeconfig.json"))["runtimeOptions"]
        if info["selfContained"]:
            frameworks = options["includedFrameworks"]
        else:
            frameworks = options.get("frameworks", []) + ([options["framework"]] if "framework" in options else [])
        required = [framework["version"] for framework in frameworks if framework["name"] == "Microsoft.NETCore.App"]
        assert len(required) == 1, "Runtime version missing"
        versions += required
    assert versions[0] == versions[1], "Desktop and Host runtime versions differ"
    if info["selfContained"]:
        native_runtime = {"win": ("coreclr.dll", "hostfxr.dll", "hostpolicy.dll"),
                          "linux": ("libcoreclr.so", "libhostfxr.so", "libhostpolicy.so"),
                          "osx": ("libcoreclr.dylib", "libhostfxr.dylib", "libhostpolicy.dylib")}[runtime.split("-")[0]]
        for name in native_runtime + ("System.Private.CoreLib.dll",):
            assert (package / name).is_file(), "Bundled runtime file missing: " + name
    locks = 0
    source_differences = []
    for packaged in (package / "dependency-locks").glob("*.packages.lock.json"):
        project = packaged.name.removesuffix(".packages.lock.json")
        source = ROOT / "src" / project / "packages.lock.json"
        before, after = read_json(source), read_json(packaged)
        if any(after["dependencies"].get(framework) != dependencies for framework, dependencies in before["dependencies"].items()):
            source_differences.append(project)
        locks += 1
    assert locks >= 6, "Dependency lock evidence is incomplete"
    if require_source_locks:
        assert not source_differences, "Source dependency pins differ: " + ", ".join(source_differences)
    if require_unified_kernel:
        assert not any(path.name.startswith("Workbench.Codex") for path in package.rglob("*")), "Legacy delegated runtime is packaged"
        for name in ("Workbench.Auth.dll", "OpenAI.dll", "Microsoft.Identity.Client.Extensions.Msal.dll", "Microsoft.IdentityModel.JsonWebTokens.dll"):
            assert (package / name).is_file(), "Unified runtime dependency missing: " + name
        for name in ("Workbench.Host.deps.json", "Workbench.Desktop.deps.json"):
            libraries = read_json(package / name)["libraries"]
            assert not any(key.startswith("Workbench.Codex/") or key.startswith("Avalonia.Headless/") for key in libraries), "Legacy or test runtime is packaged"
        licenses = {"Auth": ROOT / "src" / "Workbench.Auth" / "Licenses",
                    "Responses": ROOT / "src" / "Workbench.Core" / "Licenses" / "Responses"}
        for group, source_directory in licenses.items():
            sources = [path for path in source_directory.iterdir() if path.is_file()]
            assert len(sources) >= 2, "Source license/provenance evidence missing: " + group
            for source in sources:
                target = package / "Licenses" / group / source.name
                assert target.is_file() and digest(source) == digest(target), "License/provenance mismatch: " + str(target)
    if require_tool_upgrade:
        assert (package / "Microsoft.Extensions.FileSystemGlobbing.dll").is_file(), "Native .NET glob dependency missing"
        for group in ("Navigation", "Patch", "Commands"):
            sources = list((ROOT / "src" / "Workbench.Tools" / "Licenses" / group).glob("*"))
            assert len(sources) >= 2, "Source tool attribution missing: " + group
            for source in sources:
                target = package / "Licenses" / group / source.name
                assert target.is_file() and digest(source) == digest(target), "Tool license/provenance mismatch: " + str(target)
    if require_research:
        assert info["displayName"] == "Cogitide", "Public product identity missing"
        for name in ("LICENSE", "NOTICE", "THIRD_PARTY_NOTICES.md"):
            assert (package / name).is_file() and digest(package / name) == digest(ROOT / name), "Root license/notice mismatch: " + name
    return {"directory": str(package), "runtime": runtime, "runtimeVersion": versions[0],
            "selfContained": info["selfContained"], "verifiedFiles": len(entries), "matchingSourceLocks": locks - len(source_differences),
            "sourceLockDifferences": source_differences,
            "nativeHeadersVerified": True, "unifiedKernelVerified": require_unified_kernel, "toolUpgradeVerified": require_tool_upgrade,
            "researchReleaseNoticesVerified": require_research, "hostSmoke": {"status": "not-run"}, "guiVerified": False}


def current_runtime():
    system = {"Windows": "win", "Linux": "linux", "Darwin": "osx"}.get(platform.system())
    machine = {"amd64": "x64", "x86_64": "x64", "arm64": "arm64", "aarch64": "arm64"}.get(platform.machine().lower())
    return f"{system}-{machine}" if system and machine else None


def smoke_host(report):
    package = Path(report["directory"])
    check_root = ROOT / "artifacts" / "release-checks" / uuid.uuid4().hex
    workspace = check_root / "Unicode 工作区"
    workspace.mkdir(parents=True)
    environment = dict(os.environ)
    for name in list(environment):
        if name.startswith("DOTNET_") or name in ("DEEPSEEK_API_KEY", "OPENAI_API_KEY", "ANTHROPIC_API_KEY", "CODEX_API_KEY"):
            environment.pop(name, None)
    executable = package / ("Workbench.Host.exe" if os.name == "nt" else "Workbench.Host")
    process = subprocess.Popen([str(executable), "--data-dir", str(check_root / "isolated-state")],
        cwd=workspace, env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8", creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    incoming = queue.Queue()
    def consume():
        for line in process.stdout:
            incoming.put(json.loads(line))
        incoming.put(None)
    threading.Thread(target=consume, daemon=True).start()
    # Drain diagnostics without reflecting arbitrary stderr into the report.
    def drain_diagnostics():
        for _ in process.stderr:
            pass
    threading.Thread(target=drain_diagnostics, daemon=True).start()
    number = 0
    def request(method, parameters):
        nonlocal number
        number += 1
        identity = str(number)
        process.stdin.write(json.dumps({"id": identity, "method": method, "params": parameters}, ensure_ascii=False) + "\n")
        process.stdin.flush()
        while True:
            reply = incoming.get(timeout=30)
            assert reply is not None, "Published Host closed its protocol stream"
            if reply.get("id") == identity:
                assert not reply.get("error"), "Published Host rejected " + method
                return reply["result"]
    result = None
    try:
        initialized = request("initialize", {})
        assert initialized["protocolVersion"] == 1 and not initialized["apiKeyAvailable"]
        created = request("session.create", {"workspacePath": str(workspace), "title": "发布检查 ✓"})
        command = "Write-Output '发布校验：你好 ✓'" if os.name == "nt" else "printf '%s\\n' '发布校验：你好 ✓'"
        response = request("workspace.command", {"workspacePath": str(workspace), "command": command, "timeoutSeconds": 10})
        assert not response["isError"], "Unicode command failed"
        command_result = json.loads(response["content"])
        assert command_result["exitCode"] == 0 and "发布校验：你好 ✓" in command_result["output"]
        loaded = request("session.load", {"sessionId": created["info"]["id"]})
        assert loaded["info"]["title"] == "发布检查 ✓", "Unicode session title did not persist"
        result = {"status": "passed", "protocolVersion": initialized["protocolVersion"], "unicodeCommand": True,
                  "unicodeSession": True, "removedDotnetEnvironment": True, "modelCalls": 0,
                  "evidenceDirectory": str(check_root)}
        if os.name == "nt":
            query = ("$m = (Get-Process -Id " + str(process.pid) + ").Modules | Where-Object ModuleName -eq 'coreclr.dll'; "
                     "$m | Select-Object FileName, @{Name='version';Expression={$_.FileVersionInfo.ProductVersion}}, "
                     "@{Name='frameworkVersion';Expression={(Get-Item -LiteralPath (Join-Path (Split-Path $_.FileName) 'System.Private.CoreLib.dll')).VersionInfo.ProductVersion}} | ConvertTo-Json -Compress")
            metadata = subprocess.run(["powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", query],
                check=True, capture_output=True, text=True, encoding="utf-8", timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
            module = json.loads(metadata.stdout)
            assert Path(module["FileName"]).resolve() == package / "coreclr.dll", "Host used a runtime outside its self-contained package"
            assert module["frameworkVersion"].startswith(report["runtimeVersion"]), "Bundled runtime assembly version mismatch"
            result["loadedRuntimePath"] = module["FileName"]
            result["loadedNativeRuntimeVersion"] = module["version"]
            result["bundledRuntimeAssemblyVersion"] = module["frameworkVersion"]
    finally:
        process.stdin.close()
        try:
            process.wait(timeout=15)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)
            raise AssertionError("Published Host did not stop after stdin closed")
    assert process.returncode == 0, "Published Host exited with an error"
    result["cleanExit"] = True
    (check_root / "result.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    report["hostSmoke"] = result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directories", type=Path, nargs="+")
    parser.add_argument("--smoke-host", action="store_true", help="Run only a package matching the current OS/architecture; no model or account requests.")
    parser.add_argument("--require-source-locks", action="store_true", help="Fail if the current checkout's dependency locks differ from the package; omit when inspecting an older release.")
    parser.add_argument("--require-unified-kernel", action="store_true", help="Require OAuth/Responses dependencies, attribution and absence of the old delegated runtime.")
    parser.add_argument("--require-tool-upgrade", action="store_true", help="Require glob component and navigation/patch/command source attributions.")
    parser.add_argument("--require-research", action="store_true", help="Require Cogitide identity and root Apache/third-party notices.")
    parser.add_argument("--report", type=Path)
    args = parser.parse_args()
    source_paths = list((ROOT / "src").glob("*/packages.lock.json")) + list((ROOT / "tests").glob("*/packages.lock.json"))
    source_hashes = {str(path): digest(path) for path in source_paths}
    reports = []
    for directory in args.directories:
        report = verify_package(directory, args.require_source_locks, args.require_unified_kernel, args.require_tool_upgrade, args.require_research)
        if args.smoke_host and report["runtime"] == current_runtime():
            smoke_host(report)
        reports.append(report)
    assert source_hashes == {str(path): digest(path) for path in source_paths}, "Source lock files changed during verification"
    result = {"passed": True, "sourceLockFilesUnchangedDuringVerification": len(source_hashes), "packages": reports}
    if args.report:
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
