using System.Runtime.CompilerServices;
using System.Text.Json;
using Workbench.Core;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class CompactionTests : IDisposable
{
    private const string Summary = "## Goal\nComplete the original request.\n## Constraints\nRetain user constraints.\n## Progress\nCompleted: inspected files. Active: verify next step.\n## Key Decisions\nReuse components.\n## Next Steps\nInspect current state before acting.\n## Critical Context\nPrior observations may be stale.";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cogito-compaction-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteSessionStore _store;
    public CompactionTests() { Directory.CreateDirectory(_directory); _store = new(Path.Combine(_directory, "sessions.db")); }

    private async Task<string> Session(IReadOnlyList<ChatMessage>? history = null, CompactionCheckpoint? checkpoint = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await _store.SaveAsync(new(new(id, "Continuity", _directory, now, now), history ?? [], checkpoint));
        return id;
    }

    private static List<ChatMessage> History(int groups = 4)
    {
        List<ChatMessage> history = [new("user", "Original constraint: preserve public APIs and do not publish.")];
        for (var i = 0; i < groups; i++)
        {
            history.Add(new("assistant", "Inspect fixture", [new("old-" + i, "read", "{}")],
                Continuation: new("fixture", "https://fixture.invalid", "fixture", Wire.Element(new { secret = "PRIVATE_REASONING_MARKER" }))));
            history.Add(new("tool", new string('x', 2500), ToolCallId: "old-" + i, ToolIsError: false));
        }
        return history;
    }

    private static void CompleteToolGroups(IReadOnlyList<ChatMessage> messages)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in messages)
        {
            if (message.Role == "tool") { Assert.NotNull(message.ToolCallId); Assert.True(pending.Remove(message.ToolCallId)); }
            else
            {
                Assert.Empty(pending);
                foreach (var call in message.ToolCalls ?? []) Assert.True(pending.Add(call.Id));
            }
        }
        Assert.Empty(pending);
    }

    [Fact]
    public async Task RepeatedCompactionPreservesConstraintsPairsUsageAndColdResumeWithoutReplay()
    {
        var id = await Session();
        var tool = new FixtureTool();
        var model = new FixtureModel(toolRounds: 9);
        await new AgentEngine(_store, [tool]).RunAsync(id, "Original constraint: preserve public APIs and do not publish.", model,
            new(MaxIterations: 15, MaxContextCharacters: 8000), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        var saved = (await _store.LoadAsync(id))!;
        Assert.Equal("completed", saved.Info.Status);
        Assert.True(model.Summaries.Count >= 2);
        Assert.Equal(9, tool.Calls);
        Assert.Equal(9, saved.Messages.Count(message => message.Role == "tool"));
        Assert.NotNull(saved.Compaction);
        Assert.True(ConversationCompaction.IsValid(saved.Compaction, saved.Messages));
        Assert.All(model.Requests, request =>
        {
            CompleteToolGroups(request);
            Assert.Contains(request, message => message.Role == "user" && message.Content == "Original constraint: preserve public APIs and do not publish.");
            Assert.True(ContextWindow.Size(request) <= 8000);
        });
        Assert.All(model.Summaries, request => Assert.DoesNotContain("PRIVATE_REASONING_MARKER", JsonSerializer.Serialize(request, Wire.Json)));
        Assert.Equal(model.Summaries.Count, (await _store.EventsAsync(id)).Count(evt => evt.Type == "model.usage" && evt.Text!.Contains("compaction")));
        _store.Dispose();
        using var reopened = new SqliteSessionStore(Path.Combine(_directory, "sessions.db"));
        var resumed = new FixtureModel();
        await new AgentEngine(reopened, [tool]).RunAsync(id, "Report progress only; do not repeat actions.", resumed,
            new(MaxContextCharacters: 20000), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        Assert.Equal(9, tool.Calls);
        Assert.Empty(resumed.Summaries);
        Assert.Contains(resumed.Requests.Single(), message => message.Content.Contains("Historical context checkpoint"));
        Assert.Equal(saved.Compaction, (await reopened.LoadAsync(id))!.Compaction);
    }

    [Fact]
    public async Task CancellationDuringSecondSummaryKeepsFirstCheckpointAndNoPendingActionRuns()
    {
        var id = await Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var tool = new FixtureTool();
        var model = new FixtureModel(toolRounds: 9, beforeSummary: async (number, ct) =>
        {
            if (number == 2) { entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
        });
        var run = new AgentEngine(_store, [tool]).RunAsync(id, "Preserve public API.", model,
            new(MaxIterations: 15, MaxContextCharacters: 8000), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var checkpoint = (await _store.LoadAsync(id))!.Compaction;
        Assert.NotNull(checkpoint);
        var calls = tool.Calls;
        cancellation.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        var saved = (await _store.LoadAsync(id))!;
        Assert.Equal("cancelled", saved.Info.Status);
        Assert.Equal(checkpoint, saved.Compaction);
        Assert.Equal(calls, tool.Calls);
        Assert.Single(await _store.EventsAsync(id), evt => evt.Type == "context.compacted");
    }

    [Fact]
    public async Task PartialFailedSummaryDoesNotCommitOrRetryAndOriginalHistoryRemains()
    {
        var history = History();
        var id = await Session(history);
        var model = new FixtureModel(failSummary: true);
        await new AgentEngine(_store, []).RunAsync(id, "Report only.", model, new(MaxContextCharacters: 8000),
            (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        var saved = (await _store.LoadAsync(id))!;
        Assert.Equal("completed", saved.Info.Status);
        Assert.Null(saved.Compaction);
        Assert.Single(model.Summaries);
        Assert.Equal(history.Count + 2, saved.Messages.Count);
        Assert.Contains(model.Requests.Single(), message => message.Content == history[0].Content);
        CompleteToolGroups(model.Requests.Single());
        Assert.Single(await _store.EventsAsync(id), evt => evt.Type == "context.compaction_failed");
    }

    [Fact]
    public async Task FailedSummaryStopsIfProtectedRawHistoryCannotFit()
    {
        var history = History();
        for (var i = 1; i < history.Count; i += 2) history[i] = history[i] with { Content = new string('a', 2500) };
        var id = await Session(history);
        var model = new FixtureModel(failSummary: true);
        await new AgentEngine(_store, []).RunAsync(id, "Continue.", model, new(MaxContextCharacters: 8000),
            (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        var saved = (await _store.LoadAsync(id))!;
        Assert.Equal("failed", saved.Info.Status);
        Assert.Null(saved.Compaction);
        Assert.Empty(model.Requests);
        Assert.Single(model.Summaries);
        Assert.Equal(history.Count + 1, saved.Messages.Count);
        Assert.Contains("context budget", (await _store.EventsAsync(id))[^1].Text!);
    }

    [Fact]
    public async Task SteeringAcceptedDuringSummaryIsPresentBeforeNextNormalRequest()
    {
        var id = await Session(History());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new FixtureModel(beforeSummary: async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        var engine = new AgentEngine(_store, []);
        var run = engine.RunAsync(id, "Continue verification.", model, new(MaxContextCharacters: 8000),
            (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.SteerAsync(id, "New constraint: only report results; do not execute changes.");
        release.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("completed", (await _store.LoadAsync(id))!.Info.Status);
        var request = Assert.Single(model.Requests);
        Assert.Contains(request, message => message.Content == "New constraint: only report results; do not execute changes.");
        Assert.Contains(request, message => message.Content.StartsWith("Original constraint:"));
        CompleteToolGroups(request);
    }

    [Fact]
    public async Task ChangedHistoryInvalidatesSavedCheckpointInsteadOfTrustingIt()
    {
        var history = History(1);
        var checkpoint = new CompactionCheckpoint(1, history.Count, ConversationCompaction.Digest(history, history.Count), "FORGED_CHECKPOINT", DateTimeOffset.UtcNow);
        history[0] = new("user", "Changed requirement");
        var id = await Session(history, checkpoint);
        var model = new FixtureModel();
        await new AgentEngine(_store, []).RunAsync(id, "Report.", model, new(), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        Assert.Null((await _store.LoadAsync(id))!.Compaction);
        Assert.DoesNotContain(model.Requests.Single(), message => message.Content.Contains("FORGED_CHECKPOINT"));
        Assert.Contains(await _store.EventsAsync(id), evt => evt.Type == "context.checkpoint_invalidated");
    }

    [Fact]
    public async Task EncodedChinesePreviousSummaryCannotExceedSummaryRequestBudget()
    {
        var plan = new ConversationCompaction.Plan(1, "digest", 1000, "short transcript", new string('中', 1000), 4000);
        var model = new FixtureModel();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConversationCompaction.GenerateAsync(plan, model, CancellationToken.None));
        Assert.Empty(model.Summaries);
        Assert.Empty(model.Requests);
    }

    [Fact]
    public async Task TooManyOriginalInstructionsStopWithoutSilentlyDroppingOldRequirements()
    {
        List<ChatMessage> history = [];
        for (var i = 0; i < 10; i++)
        {
            history.Add(new("user", $"Requirement {i}: " + new string('u', 500)));
            history.Add(new("assistant", new string('a', 500)));
        }
        var id = await Session(history);
        var model = new FixtureModel();
        await new AgentEngine(_store, []).RunAsync(id, "Continue.", model, new(MaxContextCharacters: 8000),
            (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        Assert.Equal("failed", (await _store.LoadAsync(id))!.Info.Status);
        Assert.Empty(model.Requests);
        Assert.Empty(model.Summaries);
        Assert.Contains("Original user instructions", (await _store.EventsAsync(id))[^1].Text!);
        Assert.Equal(history.Count + 1, (await _store.LoadAsync(id))!.Messages.Count);
    }

    [Fact]
    public async Task ExplicitDisableKeepsExistingHistoryWindowWithoutCallingSummarizer()
    {
        var id = await Session(History());
        var model = new FixtureModel();
        await new AgentEngine(_store, []).RunAsync(id, "Report.", model, new(MaxContextCharacters: 8000, AutoCompact: false),
            (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        Assert.Empty(model.Summaries);
        Assert.Null((await _store.LoadAsync(id))!.Compaction);
        Assert.Equal("completed", (await _store.LoadAsync(id))!.Info.Status);
    }

    [Fact]
    public async Task ProjectRuleChangeWhileApprovalIsPendingRequiresNewProposal()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "AGENTS.md"), "Initial project rule.");
        var id = await Session();
        var tool = new FixtureTool(requiresApproval: true);
        var model = new FixtureModel(toolRounds: 1);
        await new AgentEngine(_store, [tool], WorkspaceService.Resolve).RunAsync(id, "Write the fixture.", model, new(),
            (_, _) => ValueTask.CompletedTask, async (_, _) =>
            {
                await File.WriteAllTextAsync(Path.Combine(_directory, "AGENTS.md"), "New rule: obtain a fresh plan.");
                return true;
            });
        Assert.Equal(0, tool.Calls);
        Assert.Contains(model.Requests[^1], message => message.Role == "system" && message.Content.Contains("New rule: obtain a fresh plan."));
        Assert.Contains(model.Requests[^1], message => message.Role == "tool" && message.Content.Contains("Not executed"));
        CompleteToolGroups(model.Requests[^1]);
    }

    [Fact]
    public async Task ProjectRulesAreReloadedAfterSlowSummaryBeforeOrdinaryModelRequest()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, "AGENTS.md"), "OLD_RULE");
        var id = await Session(History());
        var model = new FixtureModel(beforeSummary: async (_, _) => await File.WriteAllTextAsync(Path.Combine(_directory, "AGENTS.md"), "CURRENT_RULE"));
        await new AgentEngine(_store, [], WorkspaceService.Resolve).RunAsync(id, "Report.", model, new(MaxContextCharacters: 12000),
            (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        Assert.Single(model.Summaries);
        Assert.Contains(model.Requests.Single(), message => message.Role == "system" && message.Content.Contains("CURRENT_RULE"));
        Assert.DoesNotContain(model.Requests.Single(), message => message.Role == "system" && message.Content.Contains("OLD_RULE"));
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    private sealed class FixtureTool(bool requiresApproval = false) : IAgentTool
    {
        public int Calls { get; private set; }
        public ToolDefinition Definition { get; } = new("read", "Read fixture", "{\"type\":\"object\",\"properties\":{}}", requiresApproval);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new ToolResult(new string('x', 2500))); }
    }

    private sealed class FixtureModel(int toolRounds = 0, bool failSummary = false,
        Func<int, CancellationToken, Task>? beforeSummary = null) : IAgentModel
    {
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public List<IReadOnlyList<ChatMessage>> Summaries { get; } = [];
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (tools.Count == 0)
            {
                Summaries.Add(messages.ToArray());
                if (beforeSummary is not null) await beforeSummary(Summaries.Count, cancellationToken);
                if (failSummary) { yield return new(TextDelta: "## Goal\nIncomplete"); throw new IOException("Summary stream disconnected"); }
                yield return new(TextDelta: Summary, InputTokens: 400, OutputTokens: 60);
                yield break;
            }
            Requests.Add(messages.ToArray());
            if (Requests.Count <= toolRounds)
                yield return new(TextDelta: "Inspect current state.", ToolCalls: [new("new-" + Requests.Count, "read", "{}")],
                    Continuation: new("fixture", "https://fixture.invalid", "fixture", Wire.Element(new { secret = "PRIVATE_REASONING_MARKER" })));
            else yield return new(TextDelta: "Reported current progress.");
        }
    }
}
