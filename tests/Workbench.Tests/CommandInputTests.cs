using System.Text;
using System.Text.Json;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class CommandInputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-input-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Combine(_root, "workspace");
    private string State => Path.Combine(_root, "commands");
    private ToolExecutionContext Owner => new(Workspace, SessionId: "input-chat", RunId: "input-run");
    public CommandInputTests() => Directory.CreateDirectory(Workspace);
    private CommandSessionManager Manager() => new(State, WorkbenchHost.CommandWorkerStart);

    [Fact]
    public async Task PipeInputPreservesUnicodeClosesToRealEofAndCannotBeReplayed()
    {
        await using var manager = Manager();
        var command = OperatingSystem.IsWindows() ? "[Console]::Write([Console]::In.ReadToEnd())" : "cat";
        var started = Json(await manager.StartAsync(Owner, command, 30, 0, interactive: true));
        var id = started.GetProperty("commandId").GetString()!;
        var text = "🙂中文\nsecond\n";
        var receipt = Json(await manager.WriteAsync(Owner, id, text, closeInput: true));
        Assert.True(receipt.GetProperty("accepted").GetBoolean());
        Assert.Equal(Encoding.UTF8.GetByteCount(text), receipt.GetProperty("inputBytes").GetInt32());
        var terminal = await Terminal(manager, id);
        Assert.Equal("completed", terminal.GetProperty("status").GetString());
        Assert.Equal(text, terminal.GetProperty("output").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.WriteAsync(Owner, id, "replay\n"));
        Assert.Equal(0, await manager.EndRunAsync(Owner.SessionId!, Owner.RunId!));
    }

    [Fact]
    public async Task InputIsBoundedAndOwnerCheckedBeforeAnythingReachesTheShell()
    {
        await using var manager = Manager();
        var command = OperatingSystem.IsWindows() ? "[Console]::Write([Console]::In.ReadToEnd())" : "cat";
        var id = Json(await manager.StartAsync(Owner, command, 30, 0, interactive: true)).GetProperty("commandId").GetString()!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.WriteAsync(Owner with { SessionId = "foreign" }, id, "foreign\n"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.WriteAsync(Owner with { RunId = "next-run" }, id, "old-run\n"));
        await Assert.ThrowsAsync<ArgumentException>(() => manager.WriteAsync(Owner, id, new string('汉', 6000)));
        await manager.WriteAsync(Owner, id, "only-approved-input", closeInput: true);
        Assert.Equal("only-approved-input", (await Terminal(manager, id)).GetProperty("output").GetString());
    }

    [Fact]
    public async Task ExplicitSessionCommandSurvivesNormalRunEndAcceptsLaterTurnAndStopsAtChatCleanup()
    {
        await using var manager = Manager();
        var command = OperatingSystem.IsWindows()
            ? "[Console]::WriteLine('ready'); [Console]::Write([Console]::In.ReadToEnd())"
            : "printf 'ready\n'; cat";
        var id = Json(await manager.StartAsync(Owner, command, 30, 0, interactive: true, lifetime: "session"))
            .GetProperty("commandId").GetString()!;
        Assert.Equal(0, await manager.EndRunAsync(Owner.SessionId!, Owner.RunId!));
        var next = Owner with { RunId = "later-turn" };
        await manager.WriteAsync(next, id, "later-turn-input\n");
        var running = Json(await manager.ReadAsync(next, id));
        Assert.True(running.GetProperty("running").GetBoolean(), running.GetRawText());
        Assert.Equal("pipe", running.GetProperty("inputMode").GetString());
        Assert.NotEqual(JsonValueKind.Null, running.GetProperty("expiresAt").ValueKind);
        Assert.Equal(1, await manager.EndSessionAsync(Owner.SessionId!));
        var stopped = await Terminal(manager, id);
        Assert.Equal("cancelled", stopped.GetProperty("status").GetString());
        await using var reopened = Manager();
        var cold = Json(await reopened.ReadAsync(next, id));
        Assert.False(cold.GetProperty("running").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.WriteAsync(next, id, "never-replay\n"));
    }

    [Fact]
    public async Task SessionRetentionDoesNotSurviveOwnerCancellationOrExpiry()
    {
        await using var manager = Manager();
        using var cancelled = new CancellationTokenSource();
        var sleep = OperatingSystem.IsWindows() ? "Start-Sleep 60" : "sleep 60";
        var id = Json(await manager.StartAsync(Owner, sleep, 30, 0, cancelled.Token, lifetime: "session"))
            .GetProperty("commandId").GetString()!;
        cancelled.Cancel();
        Assert.Equal("cancelled", (await Terminal(manager, id)).GetProperty("status").GetString());
        var expired = Json(await manager.StartAsync(Owner, sleep, 2, 0, lifetime: "session"))
            .GetProperty("commandId").GetString()!;
        Assert.Equal("timed_out", (await Terminal(manager, expired)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task InputBackpressureIsBoundedAndStopsTheWorkerWithAnUnknownInputOutcome()
    {
        await using var manager = Manager();
        var command = OperatingSystem.IsWindows() ? "[Console]::WriteLine('not-reading'); Start-Sleep 60" : "printf 'not-reading\n'; sleep 60";
        var id = Json(await manager.StartAsync(Owner, command, 30, 0, interactive: true)).GetProperty("commandId").GetString()!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!Json(await manager.ReadAsync(Owner, id, waitMilliseconds: 30, ct: deadline.Token))
            .GetProperty("output").GetString()!.Contains("not-reading", StringComparison.Ordinal)) { }
        var failure = await Assert.ThrowsAsync<IOException>(async () =>
        {
            // A deliberately non-reading child must eventually fill its stdin pipe. One MiB
            // exceeds normal BCL pipe capacity; no extra processes or external service is needed.
            for (var index = 0; index < 64; index++) await manager.WriteAsync(Owner, id, new string('x', 16384), ct: deadline.Token);
        });
        Assert.Contains("unknown", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("cancelled", (await Terminal(manager, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task TtyUsesAnActualTerminalAndDoesNotPretendToSupportPortableHalfClose()
    {
        await using var manager = Manager();
        var command = OperatingSystem.IsWindows()
            ? "[Console]::WriteLine('TTY=' + (-not [Console]::IsInputRedirected)); $line=[Console]::ReadLine(); [Console]::WriteLine('received:' + $line)"
            : "if test -t 0; then printf 'TTY=True\n'; else printf 'TTY=False\n'; fi; IFS= read -r line; printf 'received:%s\n' \"$line\"";
        var id = Json(await manager.StartAsync(Owner, command, 30, 0, interactive: true, tty: true))
            .GetProperty("commandId").GetString()!;
        await Assert.ThrowsAsync<ArgumentException>(() => manager.WriteAsync(Owner, id, "", closeInput: true));
        await manager.WriteAsync(Owner, id, "terminal-roundtrip\r");
        var terminal = await Terminal(manager, id);
        Assert.Equal("completed", terminal.GetProperty("status").GetString());
        var output = terminal.GetProperty("output").GetString()!;
        Assert.Contains("TTY=True", output);
        Assert.Contains("received:terminal-roundtrip", output);
        Assert.Equal("pty", terminal.GetProperty("inputMode").GetString());
    }

    [Fact]
    public async Task InputAndWorktreeMutationRequireTheExistingApprovalGate()
    {
        await using var manager = Manager();
            var tools = WorkspaceTools.Create(new(), manager).Select(tool => tool.Definition).ToArray();
            foreach (var name in new[] { "write_command", "start_command", "create_worktree", "remove_worktree" })
                Assert.True(Assert.Single(tools, tool => tool.Name == name).RequiresApproval);
            foreach (var name in new[] { "read_command", "list_worktrees", "worktree_status" })
                Assert.False(Assert.Single(tools, tool => tool.Name == name).RequiresApproval);
    }

    private async Task<JsonElement> Terminal(CommandSessionManager manager, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var page = Json(await manager.ReadAsync(Owner, id, waitMilliseconds: 30, ct: deadline.Token));
            if (!page.GetProperty("running").GetBoolean()) return page;
        }
    }
    private static JsonElement Json(ToolResult result) { using var document = JsonDocument.Parse(result.Content); return document.RootElement.Clone(); }
    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(parent, comparison) || !Path.GetFileName(full).StartsWith("cogitide-input-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fixture cleanup path.");
        try { if (Directory.Exists(full)) Directory.Delete(full, true); } catch (IOException) { }
    }
}
