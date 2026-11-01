using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Workbench.Host.Isolation;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class NativeOsIsolationFactAttribute : FactAttribute
{
    public NativeOsIsolationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("COGITIDE_TEST_NATIVE_ISOLATION") != "1")
            Skip = "Explicit native OS-isolation acceptance: set COGITIDE_TEST_NATIVE_ISOLATION=1 on a supported host.";
    }
}

/// <summary>Actual native denials, not a stub or validation-only probe. No network service or model is contacted.</summary>
public sealed class OsIsolationNativeTests
{
    [NativeOsIsolationFact]
    public async Task NativeOwnershipRetainsSessionCommandsAndPreservesIncompleteCleanupEvidence()
    {
        var root = Directory.CreateDirectory(Path.Combine(OsIsolationTestPaths.PhysicalTemporaryDirectory(), "cogitide-native-life-" + Guid.NewGuid().ToString("N"))).FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(root, "host-state")).FullName;
        try
        {
            await using var manager = new OsIsolationManager(ToolWorkerStart, state);
            var status = manager.Probe();
            Assert.True(status.Supported, status.Reason);
            var context = new ToolExecutionContext(workspace, SessionId: "native-lifetime", RunId: "run-a", RequireOsIsolation: true);
            await using var first = (await manager.CreateRunToolsAsync(context))!;
            Assert.False(Assert.Single(first.Tools, t => t.Definition.Name == "read_command").Definition.RequiresApproval);
            Assert.True(Assert.Single(first.Tools, t => t.Definition.Name == "write_command").Definition.RequiresApproval);
            var start = Assert.Single(first.Tools, t => t.Definition.Name == "start_command");
            async Task<string> StartAsync(string filename, string lifetime)
            {
                var command = OperatingSystem.IsWindows()
                    ? $"[IO.File]::WriteAllText((Join-Path (Get-Location) '{filename}'), [string]$PID); Start-Sleep -Seconds 60"
                    : $"printf '%s' $$ > '{filename}'; sleep 60";
                var result = await manager.ExecuteAsync(start, new(filename, "start_command", JsonSerializer.Serialize(new
                { command, lifetime, timeoutSeconds = 90, yieldMilliseconds = 100 })), context);
                Assert.False(result.IsError, result.Content);
                using var json = JsonDocument.Parse(result.Content);
                return json.RootElement.GetProperty("commandId").GetString()!;
            }
            await StartAsync("run.pid", "run");
            var sessionId = await StartAsync("session.pid", "session");
            var runPid = await ReadPidAsync(Path.Combine(workspace, "run.pid"));
            var sessionPid = await ReadPidAsync(Path.Combine(workspace, "session.pid"));
            Assert.Equal(1, await manager.EndRunAsync(context.SessionId!, context.RunId!));
            Assert.Equal(1, await manager.EndRunAsync(context.SessionId!, context.RunId!));
            await AssertExitedAsync(runPid);
            Assert.True(IsRunning(sessionPid));
            using var cancellation = new CancellationTokenSource();
            var next = context with { RunId = "run-b" };
            await using var second = (await manager.CreateRunToolsAsync(next, cancellation.Token))!;
            var read = Assert.Single(second.Tools, t => t.Definition.Name == "read_command");
            var retained = await manager.ExecuteAsync(read, new("read", "read_command", JsonSerializer.Serialize(new { commandId = sessionId })), next);
            Assert.False(retained.IsError, retained.Content);
            using var retainedJson = JsonDocument.Parse(retained.Content);
            Assert.True(retainedJson.RootElement.GetProperty("running").GetBoolean());
            // The start RPC has long completed. The entire run token still owns the container.
            cancellation.Cancel();
            await AssertExitedAsync(sessionPid);
            await Assert.ThrowsAsync<IOException>(() => manager.ReadWorkspaceBytesAsync(workspace, "run.pid", 128));
        }
        finally { Directory.Delete(root, true); }
    }

    [NativeOsIsolationFact]
    public async Task WorkerDeniesOutsideFilesLoopbackCredentialsAndParentTermination()
    {
        var root = Directory.CreateDirectory(Path.Combine(OsIsolationTestPaths.PhysicalTemporaryDirectory(), "cogitide-native-deny-" + Guid.NewGuid().ToString("N"))).FullName;
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(root, "host-state")).FullName;
        var outside = Path.Combine(root, "outside.txt");
        var outsideWrite = Path.Combine(root, "outside-write.txt");
        var account = Path.Combine(state, "account.json");
        var canaryName = "COGITIDE_ISOLATION_CANARY_" + Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(outside, "fixture-outside-canary");
        await File.WriteAllTextAsync(account, "fixture-account-canary");
        var original = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes("native rules 😀")).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(workspace, "AGENTS.md"), original);
        Environment.SetEnvironmentVariable(canaryName, "fixture-secret-not-for-worker");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var parentFixture = StartParentFixture();
        try
        {
            await using var manager = new OsIsolationManager(ToolWorkerStart, state);
            var status = manager.Probe();
            Assert.True(status.Supported, status.Reason);
            var context = new ToolExecutionContext(workspace, SessionId: "native-chat", RunId: "native-run", RequireOsIsolation: true);
            await using var lease = (await manager.CreateRunToolsAsync(context))!;
            Assert.Equal(original, await manager.ReadWorkspaceBytesAsync(workspace, "AGENTS.md", 1024));
            await Assert.ThrowsAsync<FileNotFoundException>(() => manager.ReadWorkspaceBytesAsync(workspace, "missing", 1024));
            var shell = Assert.Single(lease.Tools, t => t.Definition.Name == "run_command");
            var script = OperatingSystem.IsWindows()
                ? WindowsDenyScript(outside, outsideWrite, account, canaryName, port, parentFixture.Id)
                : UnixDenyScript(outside, outsideWrite, account, canaryName, port, parentFixture.Id);
            var result = await manager.ExecuteAsync(shell, new("deny", "run_command", JsonSerializer.Serialize(new { command = script, timeoutSeconds = 30 })), context);
            Assert.False(result.IsError, result.Content);
            Assert.Contains("OUTSIDE_DENIED", result.Content);
            Assert.Contains("HOST_STATE_DENIED", result.Content);
            Assert.Contains("OUTSIDE_WRITE_DENIED", result.Content);
            Assert.Contains("NETWORK_DENIED", result.Content);
            Assert.Contains("PARENT_DENIED", result.Content);
            Assert.Contains("ENVIRONMENT_CLEAR", result.Content);
            if (OperatingSystem.IsWindows()) Assert.Contains("HOST_CLIPBOARD_DENIED", result.Content);
            Assert.False(File.Exists(outsideWrite));
            Assert.False(listener.Pending());
            Assert.False(parentFixture.HasExited);
            Assert.Equal("native-write", await File.ReadAllTextAsync(Path.Combine(workspace, "inside.txt")));
            await manager.EndRunAsync(context.SessionId!, context.RunId!);
            // A policy change creates a fresh worker with a native read-only workspace grant.
            var readOnly = context with { RunId = "readonly-run", ExecutionMode = ExecutionMode.ReadOnly };
            await using var readLease = (await manager.CreateRunToolsAsync(readOnly))!;
            Assert.Equal(original, await manager.ReadWorkspaceBytesAsync(readOnly, "AGENTS.md", 1024));
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReadWorkspaceBytesAsync(context, "AGENTS.md", 1024));
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReadWorkspaceBytesAsync(readOnly with { SessionId = "other-chat" }, "AGENTS.md", 1024));
            var readShell = Assert.Single(readLease.Tools, t => t.Definition.Name == "run_command");
            var write = OperatingSystem.IsWindows()
                ? "try { [IO.File]::WriteAllText((Join-Path (Get-Location) 'readonly-escape'), 'bad'); exit 77 } catch { Write-Output 'READONLY_DENIED' }"
                : "if printf bad > readonly-escape; then exit 77; else printf 'READONLY_DENIED\\n'; fi";
            var denied = await manager.ExecuteAsync(readShell, new("readonly", "run_command", JsonSerializer.Serialize(new { command = write, timeoutSeconds = 10 })), readOnly);
            Assert.False(denied.IsError, denied.Content);
            Assert.Contains("READONLY_DENIED", denied.Content);
            Assert.False(File.Exists(Path.Combine(workspace, "readonly-escape")));
        }
        finally
        {
            Environment.SetEnvironmentVariable(canaryName, null);
            if (!parentFixture.HasExited) { parentFixture.Kill(true); await parentFixture.WaitForExitAsync(); }
            Directory.Delete(root, true);
        }
    }

    private static string WindowsDenyScript(string outside, string outsideWrite, string account, string canaryName, int port, int parentPid) => $$"""
        $ErrorActionPreference = 'Stop'
        try { [void][IO.File]::ReadAllText({{Ps(outside)}}); exit 71 } catch { Write-Output 'OUTSIDE_DENIED' }
        try { [void][IO.File]::ReadAllText({{Ps(account)}}); exit 72 } catch { Write-Output 'HOST_STATE_DENIED' }
        try { [IO.File]::WriteAllText({{Ps(outsideWrite)}}, 'escape'); exit 73 } catch { Write-Output 'OUTSIDE_WRITE_DENIED' }
        $client = New-Object Net.Sockets.TcpClient
        try { $attempt = $client.ConnectAsync('127.0.0.1', {{port}}); if ($attempt.Wait(1500) -and $client.Connected) { exit 74 }; Write-Output 'NETWORK_DENIED' } catch { Write-Output 'NETWORK_DENIED' } finally { $client.Dispose() }
        try { Stop-Process -Id {{parentPid}} -Force -ErrorAction Stop; exit 75 } catch { Write-Output 'PARENT_DENIED' }
        if ([Environment]::GetEnvironmentVariable({{Ps(canaryName)}})) { exit 76 }; Write-Output 'ENVIRONMENT_CLEAR'
        Add-Type -TypeDefinition @'
        using System;
        using System.Runtime.InteropServices;
        public static class ClipboardAccessProbe {
          [DllImport("user32.dll", SetLastError=true)] private static extern bool OpenClipboard(IntPtr window);
          [DllImport("user32.dll")] private static extern bool CloseClipboard();
          public static int Probe() {
            bool opened = OpenClipboard(IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            if (opened) { CloseClipboard(); return -1; }
            return error;
          }
        }
        '@
        $clipboardError = [ClipboardAccessProbe]::Probe()
        if ($clipboardError -ne 5) { throw "Host clipboard denial was not verified; error=$clipboardError" }; Write-Output 'HOST_CLIPBOARD_DENIED'
        [IO.File]::WriteAllText((Join-Path (Get-Location) 'inside.txt'), 'native-write')
        """;

    private static string UnixDenyScript(string outside, string outsideWrite, string account, string canaryName, int port, int parentPid) => $$"""
        if cat {{Sh(outside)}} >/dev/null 2>&1; then exit 71; else printf 'OUTSIDE_DENIED\n'; fi
        if cat {{Sh(account)}} >/dev/null 2>&1; then exit 72; else printf 'HOST_STATE_DENIED\n'; fi
        if printf escape > {{Sh(outsideWrite)}}; then exit 73; else printf 'OUTSIDE_WRITE_DENIED\n'; fi
        python3 -c 'import socket,sys; s=socket.socket(); s.settimeout(1.5)
        try:
         s.connect(("127.0.0.1",{{port}}))
        except OSError:
         print("NETWORK_DENIED"); sys.exit(0)
        sys.exit(74)' || exit 74
        if kill -TERM {{parentPid}} 2>/dev/null; then exit 75; else printf 'PARENT_DENIED\n'; fi
        if printenv {{Sh(canaryName)}} >/dev/null; then exit 76; else printf 'ENVIRONMENT_CLEAR\n'; fi
        printf native-write > inside.txt
        """;

    private static string Ps(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string Sh(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    private static Process StartParentFixture()
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in OperatingSystem.IsWindows() ? new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 120" } : new[] { "-c", "sleep 120" }) start.ArgumentList.Add(argument);
        return Process.Start(start)!;
    }
    private static ProcessStartInfo ToolWorkerStart()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var dotnet = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet);
        start.ArgumentList.Add(typeof(ToolWorker).Assembly.Location);
        start.ArgumentList.Add("--tool-worker");
        return start;
    }

    private static async Task<int> ReadPidAsync(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path), out var pid)) return pid;
            await Task.Delay(100);
        }
        throw new TimeoutException("The native fixture command did not record its owned PID.");
    }
    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
    private static async Task AssertExitedAsync(int pid)
    {
        var deadline = Stopwatch.StartNew();
        while (IsRunning(pid) && deadline.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(100);
        Assert.False(IsRunning(pid), "An owned native fixture process remained alive after termination.");
    }
}
