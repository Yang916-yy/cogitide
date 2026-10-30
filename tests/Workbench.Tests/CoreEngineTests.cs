using System.Runtime.CompilerServices;
using System.Text.Json;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class CoreEngineTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "workbench-core-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteSessionStore _store;
    public CoreEngineTests() { Directory.CreateDirectory(_directory); _store = new(Path.Combine(_directory, "sessions.db")); }

    private async Task<string> Session()
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await _store.SaveAsync(new(new(id, "Test", _directory, now, now), []));
        return id;
    }

    [Fact]
    public async Task ToolLoopPersistsCompleteHistoryAndAcknowledgedRunId()
    {
        var id = await Session();
        var tool = new CountingTool();
        var engine = new AgentEngine(_store, [tool]);
        await engine.RunAsync(id, "Read the fixture", new ScriptedModel(), new(RunId: "known-run"), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        var session = await _store.LoadAsync(id);
        Assert.Equal("completed", session!.Info.Status);
        Assert.Equal(1, tool.Calls);
        Assert.Contains(session.Messages, m => m.Role == "tool" && m.ToolCallId == "call-1" && m.Content == "fixture read");
        var events = await _store.EventsAsync(id);
        Assert.All(events, e => Assert.Equal("known-run", e.RunId));
        Assert.Equal(Enumerable.Range(1, events.Count).Select(x => (long)x), events.Select(e => e.Sequence));
        Assert.Equal("run.completed", events[^1].Type);
    }

    [Fact]
    public async Task DeclinedActionNeverExecutes()
    {
        var id = await Session();
        var tool = new CountingTool(requiresApproval: true);
        var requested = false;
        await new AgentEngine(_store, [tool]).RunAsync(id, "Read", new ScriptedModel(), new(), (_, _) => ValueTask.CompletedTask,
            (_, _) => { requested = true; return Task.FromResult(false); });
        Assert.True(requested);
        Assert.Equal(0, tool.Calls);
        Assert.Contains((await _store.LoadAsync(id))!.Messages, m => m.Role == "tool" && m.Content.Contains("declined"));
    }

    [Fact]
    public async Task FailedStreamNeverExecutesEvenAnAlreadyYieldedCall()
    {
        var id = await Session();
        var tool = new CountingTool();
        await new AgentEngine(_store, [tool]).RunAsync(id, "Read", new BrokenModel(), new(), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        Assert.Equal(0, tool.Calls);
        Assert.Equal("failed", (await _store.LoadAsync(id))!.Info.Status);
        Assert.DoesNotContain((await _store.LoadAsync(id))!.Messages, m => m.ToolCalls is { Count: > 0 });
    }

    [Fact]
    public async Task CancellationPersistsUnknownOutcomeAndPreventsBlindReplay()
    {
        var id = await Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new CountingTool(onExecute: async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return "never"; });
        var engine = new AgentEngine(_store, [tool]);
        using var cancellation = new CancellationTokenSource();
        var run = engine.RunAsync(id, "Read", new ScriptedModel(), new(), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = (await _store.LoadAsync(id))!;
        Assert.Equal("cancelled", saved.Info.Status);
        Assert.Contains(saved.Messages, m => m.Role == "tool" && m.Content.Contains("may or may not"));
        await engine.RunAsync(id, "Report state only", new TextModel(), new(), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        Assert.Equal(1, tool.Calls);
    }

    [Fact]
    public async Task SameSessionCannotRunConcurrently()
    {
        var id = await Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new CountingTool(onExecute: async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return "never"; });
        var engine = new AgentEngine(_store, [tool]);
        using var cancellation = new CancellationTokenSource();
        var run = engine.RunAsync(id, "Read", new ScriptedModel(), new(), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(id, "Again", new TextModel(), new(), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true)));
        cancellation.Cancel();
        await run;
    }

    [Fact]
    public async Task IterationLimitStopsToolsAndLeavesTrace()
    {
        var id = await Session();
        var tool = new CountingTool();
        await new AgentEngine(_store, [tool]).RunAsync(id, "Read", new ScriptedModel(alwaysTool: true), new(MaxIterations: 2), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        Assert.Equal(2, tool.Calls);
        Assert.Equal("failed", (await _store.LoadAsync(id))!.Info.Status);
        Assert.Contains("iteration limit", (await _store.EventsAsync(id))[^1].Text);
    }

    [Fact]
    public async Task ContextTrimKeepsLatestUserAndCompleteToolGroups()
    {
        var id = await Session();
        var current = (await _store.LoadAsync(id))!;
        List<ChatMessage> history = [];
        for (var i = 0; i < 20; i++)
        {
            history.Add(new("user", new string('u', 400)));
            history.Add(new("assistant", "", [new ToolCall("old-" + i, "read", "{}") ]));
            history.Add(new("tool", new string('t', 400), ToolCallId: "old-" + i));
            history.Add(new("assistant", new string('a', 400)));
        }
        await _store.SaveAsync(current with { Messages = history });
        var model = new TextModel();
        await new AgentEngine(_store, []).RunAsync(id, "The latest task", model, new(MaxContextCharacters: 4000, AutoCompact: false), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        Assert.Contains(model.Seen!, m => m.Role == "user" && m.Content == "The latest task");
        var toolIds = model.Seen!.SelectMany(m => m.ToolCalls ?? []).Select(c => c.Id).Order().ToArray();
        Assert.Equal(toolIds, model.Seen!.Where(m => m.Role == "tool").Select(m => m.ToolCallId).Order().ToArray());
        Assert.Contains(await _store.EventsAsync(id), e => e.Type == "context.trimmed");
        Assert.True((await _store.LoadAsync(id))!.Messages.Count > model.Seen!.Count);
    }

    [Fact]
    public async Task BrokenSubscriberDoesNotLoseDurableCompletion()
    {
        var id = await Session();
        await new AgentEngine(_store, []).RunAsync(id, "Read", new TextModel(), new(), (_, _) => throw new IOException("Client disconnected"), (_, _) => Task.FromResult(true));
        Assert.Equal("completed", (await _store.LoadAsync(id))!.Info.Status);
        Assert.Equal("run.completed", (await _store.EventsAsync(id))[^1].Type);
    }

    [Fact]
    public async Task SteeringDuringToolCompletesCurrentToolAndSkipsPendingBatchActions()
    {
        var id = await Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new CountingTool(onExecute: async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return "current action completed"; });
        var engine = new AgentEngine(_store, [tool]);
        var model = new SteeringModel();
        var run = engine.RunAsync(id, "Original task: preserve existing files", model, new(MaxContextCharacters: 4000), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.SteerAsync(id, "Only explain the result now.");
        Assert.Contains(await _store.EventsAsync(id), e => e.Type == "input.queued");
        Assert.DoesNotContain(await _store.EventsAsync(id), e => e.Type == "input.steered");
        Assert.Equal(1, tool.Calls);
        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, tool.Calls);
        Assert.Contains(model.Seen!, m => m.Role == "user" && m.Content.StartsWith("Original task:"));
        Assert.Equal("Only explain the result now.", model.Seen![^1].Content);
        var callIds = model.Seen.SelectMany(m => m.ToolCalls ?? []).Select(c => c.Id).ToArray();
        Assert.Equal(callIds, model.Seen.Where(m => m.Role == "tool").Select(m => m.ToolCallId));
        Assert.Contains(model.Seen, m => m.Role == "tool" && m.ToolCallId == "second" && m.Content.Contains("Not executed"));
        var events = await _store.EventsAsync(id);
        Assert.True(events.Single(e => e.Type == "input.steered").Sequence > events.Last(e => e.Type == "tool.completed").Sequence);
        Assert.Equal("run.completed", events[^1].Type);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SteerAsync(id, "Too late"));
    }

    [Fact]
    public async Task SteeringWhileApprovalPendingSkipsEvenAnApprovedOldAction()
    {
        var id = await Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new CountingTool(requiresApproval: true);
        var engine = new AgentEngine(_store, [tool]);
        var model = new SteeringModel();
        var run = engine.RunAsync(id, "Inspect the files", model, new(), (_, _) => ValueTask.CompletedTask,
            async (_, ct) => { entered.TrySetResult(); return await approval.Task.WaitAsync(ct); });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.SteerAsync(id, "Do not execute any tools; explain only.");
        approval.SetResult(true);
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, tool.Calls);
        Assert.Equal(2, model.Seen!.Count(m => m.Role == "tool" && m.Content.Contains("Not executed")));
        Assert.Equal("Do not execute any tools; explain only.", model.Seen![^1].Content);
    }

    [Fact]
    public async Task SteeringAcceptedAtFinalMessageBoundaryRunsBeforeCompletion()
    {
        var id = await Session();
        var engine = new AgentEngine(_store, []);
        var model = new TextModel();
        var steered = false;
        await engine.RunAsync(id, "Explain the workspace", model, new(), async (evt, _) =>
        {
            if (evt.Type == "message.completed" && !steered)
            {
                steered = true;
                await engine.SteerAsync(id, "Also mention the verification limits.");
            }
        }, (_, _) => Task.FromResult(true));
        Assert.Equal(2, model.Requests);
        Assert.Equal("Also mention the verification limits.", model.Seen![^1].Content);
        Assert.Equal(2, (await _store.EventsAsync(id)).Count(e => e.Type == "message.completed"));
        Assert.Equal("completed", (await _store.LoadAsync(id))!.Info.Status);
    }

    [Fact]
    public async Task CancellationRetainsAcknowledgedSteeringAfterInterruptedToolResult()
    {
        var id = await Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new CountingTool(onExecute: async ct => { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); return "never"; });
        var engine = new AgentEngine(_store, [tool]);
        using var cancellation = new CancellationTokenSource();
        var run = engine.RunAsync(id, "Inspect", new ScriptedModel(), new(), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.SteerAsync(id, "Preserve this follow-up for continuation.");
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        var session = (await _store.LoadAsync(id))!;
        Assert.Equal("cancelled", session.Info.Status);
        Assert.Equal("Preserve this follow-up for continuation.", session.Messages[^1].Content);
        Assert.Equal("tool", session.Messages[^2].Role);
        Assert.Contains("may or may not", session.Messages[^2].Content);
    }

    [Fact]
    public async Task SuccessfulStreamPersistsReportedTokenUsage()
    {
        var id = await Session();
        await new AgentEngine(_store, []).RunAsync(id, "Explain", new TextModel(123, 17), new(), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        var usage = Assert.Single(await _store.EventsAsync(id), e => e.Type == "model.usage");
        using var value = JsonDocument.Parse(usage.Text!);
        Assert.Equal(123, value.RootElement.GetProperty("inputTokens").GetInt64());
        Assert.Equal(17, value.RootElement.GetProperty("outputTokens").GetInt64());
    }

    public void Dispose() { _store.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }

    private sealed class CountingTool(bool requiresApproval = false, Func<CancellationToken, Task<string>>? onExecute = null) : IAgentTool
    {
        public int Calls { get; private set; }
        public ToolDefinition Definition { get; } = new("read", "Read fixture", "{\"type\":\"object\",\"properties\":{}}", requiresApproval);
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        { Calls++; return new(onExecute is null ? "fixture read" : await onExecute(cancellationToken)); }
    }
    private sealed class ScriptedModel(bool alwaysTool = false) : IAgentModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_calls++ == 0 || alwaysTool) yield return new(ToolCalls: [new("call-" + _calls, "read", "{}")]);
            else yield return new(TextDelta: "Verified fixture.");
        }
    }
    private sealed class TextModel(long? inputTokens = null, long? outputTokens = null) : IAgentModel
    {
        public IReadOnlyList<ChatMessage>? Seen { get; private set; }
        public int Requests { get; private set; }
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Seen = messages; Requests++; await Task.Yield(); yield return new(TextDelta: "State report.", InputTokens: inputTokens, OutputTokens: outputTokens); }
    }
    private sealed class SteeringModel : IAgentModel
    {
        public IReadOnlyList<ChatMessage>? Seen { get; private set; }
        private int _requests;
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Seen = messages;
            await Task.Yield();
            if (_requests++ == 0) yield return new(ToolCalls: [new("first", "read", "{}"), new("second", "read", "{}")]);
            else yield return new(TextDelta: "Followed the additional instructions.");
        }
    }
    private sealed class BrokenModel : IAgentModel
    {
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.Yield(); yield return new(ToolCalls: [new("call-1", "read", "{}")]); throw new IOException("Stream lost"); }
    }
}
