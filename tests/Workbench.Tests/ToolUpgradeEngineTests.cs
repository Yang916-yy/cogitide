using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class ToolUpgradeEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogito-tool-engine-" + Guid.NewGuid().ToString("N"));
    public ToolUpgradeEngineTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AllPatchScopesArePresentedAndChangedRulesAfterApprovalPreventWrites()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        Directory.CreateDirectory(Path.Combine(_root, "b"));
        File.WriteAllText(Path.Combine(_root, "a", "AGENTS.md"), "RULE_A: preserve public signatures");
        File.WriteAllText(Path.Combine(_root, "b", "AGENTS.md"), "RULE_B: test affected behavior");
        File.WriteAllText(Path.Combine(_root, "a", "file.txt"), "before\n");
        File.WriteAllText(Path.Combine(_root, "b", "file.txt"), "before\n");
        var service = new WorkspaceService();
        var first = Json(await service.ReadFileAsync(_root, "a/file.txt"));
        var second = Json(await service.ReadFileAsync(_root, "b/file.txt"));
        var patch = "*** Begin Patch\n*** Update File: a/file.txt\n@@\n-before\n+after\n*** Update File: b/file.txt\n@@\n-before\n+after\n*** End Patch";
        var arguments = JsonSerializer.Serialize(new { patchText = patch, expectedHashes = new Dictionary<string, string>
            { ["a/file.txt"] = first.GetProperty("sha256").GetString()!, ["b/file.txt"] = second.GetProperty("sha256").GetString()! } });
        using var store = new SqliteSessionStore(Path.Combine(_root, "session.db"));
        var session = await Create(store);
        var step = 0;
        var model = new Model((messages, _) =>
        {
            step++;
            if (step == 2)
            {
                Assert.Contains("RULE_A", messages[0].Content);
                Assert.Contains("RULE_B", messages[0].Content);
                Assert.Contains("new or changed", messages.Last().Content);
            }
            if (step == 3)
            {
                Assert.Contains("REVIEW_CHANGED", messages[0].Content);
                Assert.Contains("new or changed", messages.Last().Content);
                Assert.Equal("before\n", File.ReadAllText(Path.Combine(_root, "a", "file.txt")));
            }
            return step <= 3 ? new(ToolCalls: [new("patch-" + step, "apply_patch", arguments)]) : new(TextDelta: "Both updated.");
        });
        var approvals = 0;
        var events = new List<AgentEvent>();
        await new AgentEngine(store, WorkspaceTools.Create(service), WorkspaceService.Resolve).RunAsync(session.Info.Id, "Update both files.",
            model, new(), (evt, _) => { events.Add(evt); return ValueTask.CompletedTask; },
            (_, _) =>
            {
                if (++approvals == 1) File.WriteAllText(Path.Combine(_root, "b", "AGENTS.md"), "REVIEW_CHANGED: recheck before any write");
                return Task.FromResult(true);
            });
        Assert.Equal(2, approvals);
        Assert.Equal("after\n", File.ReadAllText(Path.Combine(_root, "a", "file.txt")));
        Assert.Equal("after\n", File.ReadAllText(Path.Combine(_root, "b", "file.txt")));
        Assert.Equal("run.completed", events.Last().Type);
        Assert.Equal(2, events.Count(evt => evt.Type == "tool.completed" && evt.Result!.IsError));
        var ledger = new EvidenceMemory(session.Info.Id, _root);
        foreach (var evt in await store.EventsAsync(session.Info.Id)) ledger.Observe(evt);
        Assert.Equal(2, ledger.Snapshot().Entries.Last().Artifacts!.Count);
    }

    [Fact]
    public async Task AgentCannotFinishSuccessfullyWithUnfinishedBackgroundCommand()
    {
        using var store = new SqliteSessionStore(Path.Combine(_root, "session.db"));
        var session = await Create(store);
        await using var commands = new CommandSessionManager(Path.Combine(_root, "command-state"), WorkbenchHost.CommandWorkerStart);
        var count = 0;
        string? id = null;
        var model = new Model((messages, tools) =>
        {
            Assert.Equal(17, tools.Count);
            if (++count == 1) return new(ToolCalls: [new("launch", "start_command", JsonSerializer.Serialize(new
                { command = OperatingSystem.IsWindows() ? "Start-Sleep 60" : "sleep 60", timeoutSeconds = 60, yieldMilliseconds = 0 }))]);
            using var result = JsonDocument.Parse(messages.Last().Content);
            id = result.RootElement.GetProperty("commandId").GetString();
            return new(TextDelta: "Finished prematurely.");
        });
        var events = new List<AgentEvent>();
        await new AgentEngine(store, WorkspaceTools.Create(new(), commands), WorkspaceService.Resolve, commands)
            .RunAsync(session.Info.Id, "Run a check.", model, new(), (evt, _) => { events.Add(evt); return ValueTask.CompletedTask; },
                (_, _) => Task.FromResult(true));
        Assert.Equal("run.failed", events.Last().Type);
        Assert.Contains(events, evt => evt.Type == "commands.stopped");
        var output = Json(await commands.ReadAsync(new(_root, SessionId: session.Info.Id, RunId: "next-run"), id!));
        Assert.False(output.GetProperty("running").GetBoolean());
        Assert.NotEqual("completed", output.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReadOnlyOmitsPatchAndLaunchButRetainsOwnedOutputAndStop()
    {
        using var store = new SqliteSessionStore(Path.Combine(_root, "session.db"));
        var session = await Create(store);
        await using var commands = new CommandSessionManager(Path.Combine(_root, "command-state"), WorkbenchHost.CommandWorkerStart);
        var model = new Model((_, tools) =>
        {
            Assert.Equal(12, tools.Count);
            Assert.DoesNotContain(tools, tool => tool.Name is "start_command" or "apply_patch" or "run_command" or "write_file" or "replace_text");
            Assert.Contains(tools, tool => tool.Name == "read_command");
            Assert.Contains(tools, tool => tool.Name == "stop_command");
            return new(TextDelta: "Read-only policy confirmed.");
        });
        await new AgentEngine(store, WorkspaceTools.Create(new(), commands), WorkspaceService.Resolve, commands)
            .RunAsync(session.Info.Id, "Inspect only.", model, new(ExecutionMode: ExecutionMode.ReadOnly), (_, _) => ValueTask.CompletedTask,
                (_, _) => throw new InvalidOperationException("No mutation can be approved in this fixture."));
    }

    [Fact]
    public async Task CleanupFailurePersistsFailedTerminalInsteadOfClaimingCompletion()
    {
        using var store = new SqliteSessionStore(Path.Combine(_root, "session.db"));
        var session = await Create(store);
        var events = new List<AgentEvent>();
        await new AgentEngine(store, [], WorkspaceService.Resolve, new FailingLifetime()).RunAsync(session.Info.Id, "Check.",
            new Model((_, _) => new(TextDelta: "Done.")), new(), (evt, _) => { events.Add(evt); return ValueTask.CompletedTask; },
            (_, _) => Task.FromResult(false));
        Assert.Equal("run.failed", events.Last().Type);
        Assert.Equal("failed", (await store.LoadAsync(session.Info.Id))!.Info.Status);
        Assert.Contains(events, evt => evt.Type == "commands.cleanup_failed");
        Assert.DoesNotContain(events, evt => evt.Type == "run.completed");
    }

    [Fact]
    public async Task OutputAndResultBudgetsPersistOnlyCompleteUnicodeScalars()
    {
        using var store = new SqliteSessionStore(Path.Combine(_root, "session.db"));
        var session = await Create(store);
        var step = 0;
        await new AgentEngine(store, [new UnicodeTool()], WorkspaceService.Resolve).RunAsync(session.Info.Id, "Check Unicode.",
            new Model((_, _) => ++step == 1 ? new(ToolCalls: [new("unicode", "unicode_fixture", "{}")]) : new(TextDelta: "Done.")),
            new(MaxContextCharacters: 500000), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        var persisted = await store.EventsAsync(session.Info.Id);
        var output = persisted.Where(evt => evt.Type == "tool.output").ToArray();
        Assert.NotEmpty(output);
        Assert.InRange(output.Sum(evt => evt.Text!.Length), 99000, 100000);
        foreach (var evt in output) Assert.DoesNotContain('\uFFFD', evt.Text!);
        var result = persisted.Single(evt => evt.Type == "tool.completed").Result!.Content;
        Assert.DoesNotContain('\uFFFD', result);
        Assert.Contains("Output truncated", result);
    }

    private sealed class FailingLifetime : IRunResourceLifetime
    {
        public Task<int> EndRunAsync(string sessionId, string runId) => throw new UnauthorizedAccessException("Fixture metadata unavailable.");
    }
    private sealed class UnicodeTool : IAgentTool
    {
        public ToolDefinition Definition => new("unicode_fixture", "Unicode budget fixture", "{\"type\":\"object\"}", false);
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            for (var i = 0; i < 26; i++) await context.OnOutput!(new string('x', 3999) + "😀", cancellationToken);
            return new(new string('x', 99999) + "😀");
        }
    }

    private async Task<AgentSession> Create(ISessionStore store)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new AgentSession(new(Guid.NewGuid().ToString("N"), "tool upgrade", _root, now, now), []);
        await store.SaveAsync(session);
        return session;
    }
    private static JsonElement Json(ToolResult result) { using var doc = JsonDocument.Parse(result.Content); return doc.RootElement.Clone(); }
    private sealed class Model(Func<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolDefinition>, ModelUpdate> next) : IAgentModel
    {
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return next(messages, tools);
            await Task.CompletedTask;
        }
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
