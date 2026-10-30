using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Workbench.Tools;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class CommandSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogito-command-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Combine(_root, "workspace");
    private string State => Path.Combine(_root, "commands");
    private ToolExecutionContext Owner => new(Workspace, SessionId: "chat-one", RunId: "run-one");
    public CommandSessionTests() => Directory.CreateDirectory(Workspace);
    private CommandSessionManager Manager() => new(State, WorkbenchHost.CommandWorkerStart);

    [Fact]
    public async Task BackgroundReturnsPromptlyPreservesPagedUnicodeAndColdReadsWithoutReplay()
    {
        string id;
        var expected = new string('x', 2047) + "😀中文" + new string('y', 90000) + "🏁";
        await File.WriteAllTextAsync(Path.Combine(Workspace, "expected.txt"), expected, new UTF8Encoding(false));
        await using (var manager = Manager())
        {
            var command = OperatingSystem.IsWindows()
                ? "Add-Content -LiteralPath marker.txt -Value 'once'; while (!(Test-Path -LiteralPath gate.txt)) { Start-Sleep -Milliseconds 20 }; [Console]::Write([IO.File]::ReadAllText((Join-Path (Get-Location) 'expected.txt')))"
                : "printf 'once\n' >> marker.txt; while [ ! -f gate.txt ]; do sleep 0.02; done; cat expected.txt";
            var started = Json(await manager.StartAsync(Owner, command, 30, 0));
            id = started.GetProperty("commandId").GetString()!;
            Assert.True(started.GetProperty("running").GetBoolean());
            // Prove the caller can do independent work while the shell is still blocked.
            await new WorkspaceService().WriteFileAsync(Workspace, "gate.txt", "release", "");
            var terminal = await Terminal(manager, id);
            Assert.True(terminal.GetProperty("status").GetString() == "completed", terminal.GetRawText());
            Assert.Equal(0, terminal.GetProperty("exitCode").GetInt32());
            var text = new StringBuilder();
            long offset = 0;
            do
            {
                var page = Json(await manager.ReadAsync(Owner, id, offset, 4097));
                text.Append(page.GetProperty("output").GetString());
                var next = page.GetProperty("nextOffset").GetInt64();
                if (!page.GetProperty("hasMore").GetBoolean()) break;
                Assert.True(next > offset);
                offset = next;
            } while (true);
            Assert.Equal(expected, text.ToString());
            var end = Encoding.UTF8.GetByteCount(expected);
            await Assert.ThrowsAsync<ArgumentException>(() => manager.ReadAsync(Owner, id, end - 1));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.ReadAsync(Owner with { SessionId = "other-chat" }, id));
            Assert.Equal(0, await manager.EndRunAsync(Owner.SessionId!, Owner.RunId!));
        }
        await using var reopened = Manager();
        var restored = Json(await reopened.ReadAsync(Owner with { RunId = "next-run" }, id));
        Assert.Equal("completed", restored.GetProperty("status").GetString());
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(Workspace, "marker.txt")));
    }

    [Fact]
    public async Task WorkerReplacesSnapshotWhileAnExistingReaderOnlySharesDelete()
    {
        FileStream? previousSnapshot = null;
        await using var manager = new CommandSessionManager(State, () =>
        {
            // StartAsync has persisted "starting" before asking for its worker. Hold that inode/file
            // throughout worker startup and completion, as an external snapshot reader could do.
            var directory = Assert.Single(Directory.EnumerateDirectories(State));
            previousSnapshot = new FileStream(Path.Combine(directory, "session.json"), FileMode.Open,
                FileAccess.Read, FileShare.Delete);
            return WorkbenchHost.CommandWorkerStart();
        });
        try
        {
            var command = OperatingSystem.IsWindows()
                ? "Add-Content -LiteralPath marker.txt -Value 'once'; [Console]::Write('snapshot-published')"
                : "printf 'once\n' >> marker.txt; printf 'snapshot-published'";
            var started = Json(await manager.StartAsync(Owner, command, 30, 10000));
            var id = started.GetProperty("commandId").GetString()!;
            var terminal = started.GetProperty("running").GetBoolean() ? await Terminal(manager, id) : started;
            Assert.True(terminal.GetProperty("status").GetString() == "completed", terminal.GetRawText());
            Assert.Equal(0, terminal.GetProperty("exitCode").GetInt32());
            Assert.Equal("snapshot-published", terminal.GetProperty("output").GetString());
            Assert.Single(await File.ReadAllLinesAsync(Path.Combine(Workspace, "marker.txt")));

            Assert.NotNull(previousSnapshot);
            using var old = await JsonDocument.ParseAsync(previousSnapshot);
            Assert.Equal("starting", old.RootElement.GetProperty("status").GetString());
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(State, id), "*.tmp"));
        }
        finally { previousSnapshot?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotReadReopensAfterTransientMissingNameOrSharingConflict(bool sharingConflict)
    {
        if (!OperatingSystem.IsWindows()) return;
        var (id, metadata) = await CreateColdRecordAsync();
        await using var manager = new CommandSessionManager(State, () => throw new InvalidOperationException("A read must never start or replay a worker."));
        var parked = metadata + ".parked";
        FileStream? blocker = null;
        if (sharingConflict) blocker = new(metadata, FileMode.Open, FileAccess.Read, FileShare.None);
        else File.Move(metadata, parked);
        try
        {
            // The first open happens before the method yields. Restore the name/release the lock only
            // after that first attempt failed, without guessing worker scheduling using a sleep.
            var pending = manager.ReadAsync(Owner, id);
            Assert.False(pending.IsCompleted);
            if (sharingConflict) { blocker!.Dispose(); blocker = null; }
            else File.Move(parked, metadata);
            var result = Json(await pending.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("completed", result.GetProperty("status").GetString());
            Assert.Equal(0, result.GetProperty("exitCode").GetInt32());
            Assert.False(File.Exists(Path.Combine(Workspace, "marker.txt")));
        }
        finally { blocker?.Dispose(); }
    }

    [Fact]
    public async Task MissingSnapshotRemainsAnErrorAfterBoundedReopens()
    {
        var (id, metadata) = await CreateColdRecordAsync();
        await using var manager = new CommandSessionManager(State, () => throw new InvalidOperationException("A read must never replay a worker."));
        File.Delete(metadata);
        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.ReadAsync(Owner, id).WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task SnapshotReopenWaitCanBeCancelledAndDoesNotHideMalformedRecords()
    {
        var (id, metadata) = await CreateColdRecordAsync();
        await using var manager = new CommandSessionManager(State, () => throw new InvalidOperationException("A read must never replay a worker."));
        if (OperatingSystem.IsWindows())
        {
            var parked = metadata + ".parked";
            File.Move(metadata, parked);
            using var cancellation = new CancellationTokenSource();
            var pending = manager.ReadAsync(Owner, id, ct: cancellation.Token);
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            File.Move(parked, metadata);
        }
        await File.WriteAllTextAsync(metadata, "{invalid-json");
        await Assert.ThrowsAsync<JsonException>(() => manager.ReadAsync(Owner, id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndExplicitStopTerminateOwnedShellAndChild(bool explicitStop)
    {
        await using var manager = Manager();
        using var cancellation = new CancellationTokenSource();
        var command = OperatingSystem.IsWindows()
            ? "$ownedChild = Start-Process -FilePath powershell.exe -ArgumentList '-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru; Write-Output ('ROOT=' + $PID + ';CHILD=' + $ownedChild.Id); Wait-Process -Id $ownedChild.Id"
            : "sleep 60 & child=$!; printf 'ROOT=%s;CHILD=%s\n' \"$$\" \"$child\"; wait \"$child\"";
        var id = Json(await manager.StartAsync(Owner, command, 60, 0, cancellation.Token)).GetProperty("commandId").GetString()!;
        var match = await WaitForPids(manager, id);
        using var root = Process.GetProcessById(int.Parse(match.Groups[1].Value));
        using var child = Process.GetProcessById(int.Parse(match.Groups[2].Value));
        if (explicitStop) await manager.StopAsync(Owner, id);
        else cancellation.Cancel();
        var result = await Terminal(manager, id);
        Assert.Equal("cancelled", result.GetProperty("status").GetString());
        Assert.True(result.GetProperty("exitCode").ValueKind == JsonValueKind.Null);
        await Task.WhenAll(root.WaitForExitAsync(), child.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(root.HasExited && child.HasExited);
    }

    [Fact]
    public async Task WorkerLosesParentPipeAndCancelsRatherThanDetaching()
    {
        await using var manager = Manager();
        var id = Json(await manager.StartAsync(Owner, OperatingSystem.IsWindows()
            ? "Write-Output ('ROOT=' + $PID + ';CHILD=' + $PID); Start-Sleep 60"
            : "printf 'ROOT=%s;CHILD=%s\n' \"$$\" \"$$\"; sleep 60", 60, 0)).GetProperty("commandId").GetString()!;
        var pids = await WaitForPids(manager, id);
        using var shell = Process.GetProcessById(int.Parse(pids.Groups[1].Value));
        // EndRun closes the same stdin ownership pipe the OS closes when a Host crashes.
        Assert.Equal(1, await manager.EndRunAsync(Owner.SessionId!, Owner.RunId!));
        await shell.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("cancelled", (await Terminal(manager, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task TimeoutAndFailureAreTerminalNotSuccessfulVerification()
    {
        await using var manager = Manager();
        var id = Json(await manager.StartAsync(Owner, OperatingSystem.IsWindows() ? "Start-Sleep 30" : "sleep 30", 1, 0))
            .GetProperty("commandId").GetString()!;
        var timed = await Terminal(manager, id);
        Assert.True(timed.GetProperty("timedOut").GetBoolean());
        Assert.False(timed.GetProperty("running").GetBoolean());
        var failed = Json(await manager.StartAsync(Owner, "exit 7", 10, 0)).GetProperty("commandId").GetString()!;
        var terminal = await Terminal(manager, failed);
        Assert.Equal("failed", terminal.GetProperty("status").GetString());
        Assert.Equal(7, terminal.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData("broken fixture")]
    [InlineData("{}")]
    public async Task RunCleanupStillStopsWorkersIfOneMetadataRecordIsCorrupted(string invalidRecord)
    {
        await using var manager = Manager();
        var command = OperatingSystem.IsWindows() ? "Write-Output ('ROOT=' + $PID + ';CHILD=' + $PID); Start-Sleep 60"
            : "printf 'ROOT=%s;CHILD=%s\n' \"$$\" \"$$\"; sleep 60";
        var id = Json(await manager.StartAsync(Owner, command, 60, 0)).GetProperty("commandId").GetString()!;
        var pids = await WaitForPids(manager, id);
        using var shell = Process.GetProcessById(int.Parse(pids.Groups[1].Value));
        await File.WriteAllTextAsync(Path.Combine(State, id, "session.json"), invalidRecord);
        Assert.Equal(1, await manager.EndRunAsync(Owner.SessionId!, Owner.RunId!));
        await shell.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task UnreadableMetadataDoesNotPreventWorkerShutdown()
    {
        await using var manager = Manager();
        var command = OperatingSystem.IsWindows() ? "Write-Output ('ROOT=' + $PID + ';CHILD=' + $PID); Start-Sleep 60"
            : "printf 'ROOT=%s;CHILD=%s\n' \"$$\" \"$$\"; sleep 60";
        var id = Json(await manager.StartAsync(Owner, command, 60, 0)).GetProperty("commandId").GetString()!;
        var pids = await WaitForPids(manager, id);
        using var shell = Process.GetProcessById(int.Parse(pids.Groups[1].Value));
        var metadata = Path.Combine(State, id, "session.json");
        File.Delete(metadata);
        Directory.CreateDirectory(metadata); // File open now throws UnauthorizedAccessException on Windows.
        await Assert.ThrowsAnyAsync<Exception>(() => manager.EndRunAsync(Owner.SessionId!, Owner.RunId!));
        await shell.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(shell.HasExited);
    }

    [Fact]
    public async Task LargeCommandAndEscapedOutputKeepEveryPageValidAndLossless()
    {
        await using var manager = Manager();
        var command = (OperatingSystem.IsWindows() ? "[Console]::Write(('汉' * 12000)); #"
            : "i=0; while [ $i -lt 12000 ]; do printf '汉'; i=$((i+1)); done; #") + new string('x', 30000);
        var started = await manager.StartAsync(Owner, command, 20, 0);
        Assert.InRange(started.Content.Length, 1, 80000);
        var id = Json(started).GetProperty("commandId").GetString()!;
        var terminal = await Terminal(manager, id);
        Assert.True(terminal.GetProperty("status").GetString() == "completed",
            terminal.GetProperty("status").GetString() + ": " + terminal.GetProperty("error").ToString());
        var content = new StringBuilder();
        long offset = 0;
        while (true)
        {
            var result = await manager.ReadAsync(Owner, id, offset);
            Assert.InRange(result.Content.Length, 1, 80000);
            var page = Json(result);
            content.Append(page.GetProperty("output").GetString());
            if (!page.GetProperty("hasMore").GetBoolean()) break;
            var next = page.GetProperty("nextOffset").GetInt64();
            Assert.True(next > offset);
            offset = next;
        }
        Assert.Equal(new string('汉', 12000), content.ToString());
        var directories = Directory.GetDirectories(State).Length;
        await Assert.ThrowsAsync<ArgumentException>(() => manager.StartAsync(Owner, "#" + new string('汉', 10000)));
        Assert.Equal(directories, Directory.GetDirectories(State).Length);
    }

    [Fact]
    public async Task OutputSinkFailureStopsProcessBeforePropagatingError()
    {
        Process? owned = null;
        try
        {
            var command = OperatingSystem.IsWindows() ? "Write-Output ('ROOT=' + $PID); Start-Sleep 60"
                : "printf 'ROOT=%s\n' \"$$\"; sleep 60";
            await Assert.ThrowsAsync<IOException>(() => new WorkspaceService().RunCommandAsync(Workspace, command, 60,
                onOutput: (chunk, _) =>
                {
                    var match = Regex.Match(chunk, @"ROOT=(\d+)");
                    if (match.Success) owned = Process.GetProcessById(int.Parse(match.Groups[1].Value));
                    throw new IOException("Generated output sink failure.");
                }).WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.NotNull(owned);
            await owned.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { owned?.Dispose(); }
    }

    private async Task<Match> WaitForPids(CommandSessionManager manager, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var result = Json(await manager.ReadAsync(Owner, id, waitMilliseconds: 50, ct: deadline.Token));
            var match = Regex.Match(result.GetProperty("output").GetString()!, @"ROOT=(\d+);CHILD=(\d+)");
            if (match.Success) return match;
            Assert.True(result.GetProperty("running").GetBoolean(), result.GetRawText());
        }
    }
    private async Task<(string Id, string Metadata)> CreateColdRecordAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(State, id);
        Directory.CreateDirectory(directory);
        var metadata = Path.Combine(directory, "session.json");
        var now = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(new
        {
            id, sessionId = Owner.SessionId, runId = Owner.RunId, workspacePath = Workspace,
            command = "metadata-fixture-never-execute", status = "completed", startedAt = now,
            exitCode = 0, outputTruncated = false, finishedAt = now
        }, Wire.Json));
        return (id, metadata);
    }
    private async Task<JsonElement> Terminal(CommandSessionManager manager, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var result = Json(await manager.ReadAsync(Owner, id, waitMilliseconds: 30, ct: deadline.Token));
            if (!result.GetProperty("running").GetBoolean()) return result;
        }
    }
    private static JsonElement Json(ToolResult result) { using var document = JsonDocument.Parse(result.Content); return document.RootElement.Clone(); }
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
}
