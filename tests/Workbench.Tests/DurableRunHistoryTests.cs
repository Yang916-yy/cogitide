using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class DurableRunHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cogitide-durable-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "sessions.db");
    private static readonly Func<AgentEvent, CancellationToken, ValueTask> Emit = (_, _) => ValueTask.CompletedTask;
    private static readonly Func<ApprovalRequest, CancellationToken, Task<bool>> Approve = (_, _) => Task.FromResult(true);

    public DurableRunHistoryTests() => Directory.CreateDirectory(_directory);

    private async Task<string> Create(SqliteSessionStore store, IReadOnlyList<ChatMessage>? messages = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new(new(id, "Durable task", _directory, now, now), messages ?? []));
        return id;
    }

    [Fact]
    public async Task IterationPauseSurvivesReopenAndContinuationUsesOriginalInstructionsWithoutReplay()
    {
        var tool = new CountTool();
        string id;
        using (var store = new SqliteSessionStore(Database))
        {
            id = await Create(store);
            await new AgentEngine(store, [tool]).RunAsync(id, "Original instruction: preserve all public interfaces.",
                new CallbackModel((_, _) => new(ToolCalls: [new("first", "read_fixture", "{}")], InputTokens: 11, OutputTokens: 3)),
                new(MaxIterations: 1, RunId: "first-run"), Emit, Approve);
            var saved = (await store.LoadAsync(id))!;
            Assert.Equal("paused", saved.Info.Status);
            Assert.Equal("iteration_limit", saved.RunCheckpoint!.StopReason);
            Assert.Equal(14, saved.RunCheckpoint.Usage!.ReportedTokens);
            Assert.Equal("run.paused", (await store.EventsAsync(id))[^1].Type);
        }
        using var reopened = new SqliteSessionStore(Database);
        var continuation = new CallbackModel((messages, _) =>
        {
            Assert.Contains(messages, message => message.Content == "Original instruction: preserve all public interfaces.");
            Assert.Contains(messages, message => message.Role == "tool" && message.ToolCallId == "first" && message.Content == "observed");
            return new(TextDelta: "The observed result is retained.", InputTokens: 9, OutputTokens: 2);
        });
        await new AgentEngine(reopened, [tool]).ContinueAsync(id, continuation, new(RunId: "second-run"), Emit, Approve);
        var completed = (await reopened.LoadAsync(id))!;
        Assert.Equal("completed", completed.Info.Status);
        Assert.Equal("first-run", completed.RunCheckpoint!.ParentRunId);
        Assert.Equal(0, completed.RunCheckpoint.ActiveStartMessageIndex);
        Assert.Equal(1, tool.Calls);
        Assert.Single(completed.Messages, message => message.Role == "user");
        Assert.Equal(11, completed.RunCheckpoint.Usage!.ReportedTokens); // New run, fresh explicit allowance.
    }

    [Fact]
    public async Task FailedStreamRetainsReportedUsageAndDiscardsPartialToolAndNativeState()
    {
        using var store = new SqliteSessionStore(Database);
        var id = await Create(store);
        var tool = new CountTool();
        await new AgentEngine(store, [tool]).RunAsync(id, "Inspect", new FaultModel(), new(), Emit, Approve);
        var saved = (await store.LoadAsync(id))!;
        Assert.Equal("failed", saved.Info.Status);
        Assert.Equal(25, saved.RunCheckpoint!.Usage!.ReportedTokens);
        Assert.Equal(1, saved.RunCheckpoint.Usage.ModelCalls);
        Assert.Equal(1, saved.RunCheckpoint.Usage.IncompleteCalls);
        Assert.DoesNotContain(saved.Messages, message => message.Continuation is not null || message.ToolCalls is { Count: > 0 });
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task AcceptedPromptAndCheckpointAreDurableBeforeExternalInitialization()
    {
        using var store = new SqliteSessionStore(Database);
        var id = await Create(store);
        var engine = new AgentEngine(store, [], additionalToolsFactory: async (_, _, _) =>
        {
            var saved = (await store.LoadAsync(id))!;
            Assert.Equal("Durable before initialization", Assert.Single(saved.Messages).Content);
            Assert.Equal("startup-run", saved.RunCheckpoint!.RunId);
            throw new IOException("Fixture initialization failure.");
        });
        await Assert.ThrowsAsync<IOException>(() => engine.RunAsync(id, "Durable before initialization",
            new CallbackModel((_, _) => throw new InvalidOperationException("No model call is allowed.")),
            new(RunId: "startup-run"), Emit, Approve));
        Assert.Equal(1, await store.RecoverInterruptedAsync());
        await engine.ContinueAsync(id, new CallbackModel((messages, _) =>
        {
            Assert.Single(messages, message => message.Content == "Durable before initialization");
            return new(TextDelta: "Recovered safely.");
        }), new(ExecutionMode: ExecutionMode.ReadOnly), Emit, Approve);
        Assert.Equal("completed", (await store.LoadAsync(id))!.Info.Status);
    }

    [Theory]
    [InlineData(false, "reported_token_limit")]
    [InlineData(true, "token_usage_unavailable")]
    public async Task ReportedTokenCapStopsBeforePendingToolsAndPreservesUnknownUsage(bool unknown, string reason)
    {
        using var store = new SqliteSessionStore(Database);
        var id = await Create(store);
        var tool = new CountTool();
        var model = new CallbackModel((_, _) => new(ToolCalls: [new("unexecuted", "read_fixture", "{}")],
            InputTokens: unknown ? null : 9, OutputTokens: unknown ? null : 3));
        await new AgentEngine(store, [tool]).RunAsync(id, "Inspect", model, new(MaxReportedTokens: 10), Emit, Approve);
        var saved = (await store.LoadAsync(id))!;
        Assert.Equal("paused", saved.Info.Status);
        Assert.Equal(reason, saved.RunCheckpoint!.StopReason);
        Assert.Equal(unknown ? 1 : 0, saved.RunCheckpoint.Usage!.CallsWithUnknownUsage);
        Assert.Equal(0, tool.Calls);
        Assert.DoesNotContain(saved.Messages, message => message.ToolCalls is { Count: > 0 });
    }

    [Fact]
    public async Task ReportedUsageUpdatesAreCumulativePerCallAndCompactionCountsAgainstTheSameCap()
    {
        using var store = new SqliteSessionStore(Database);
        List<ChatMessage> history = [new("user", "Original instruction: retain the API.")];
        for (var index = 0; index < 6; index++)
        {
            history.Add(new("assistant", "read", [new("old-" + index, "read_fixture", "{}")]));
            history.Add(new("tool", new string('x', 2500), ToolCallId: "old-" + index));
        }
        var id = await Create(store, history);
        var summary = new SummaryUsageModel();
        await new AgentEngine(store, []).RunAsync(id, "Report current progress.", summary,
            new(MaxContextCharacters: 8000, MaxReportedTokens: 20), Emit, Approve);
        var saved = (await store.LoadAsync(id))!;
        Assert.Equal("paused", saved.Info.Status);
        Assert.Null(saved.Compaction);
        Assert.Equal("reported_token_limit", saved.RunCheckpoint!.StopReason);
        Assert.Equal(20, saved.RunCheckpoint.Usage!.ReportedTokens); // 15+5, not 10+2+15+5.
        Assert.Equal(1, summary.Calls);
        Assert.Equal(history.Count + 1, saved.Messages.Count);
    }

    [Fact]
    public async Task TimeLimitAndCancellationRemainDistinctAndUnknownActionsAreNeverReplayed()
    {
        using var store = new SqliteSessionStore(Database);
        var id = await Create(store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tool = new CountTool(async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
        var engine = new AgentEngine(store, [tool]);
        var run = engine.RunAsync(id, "Inspect", new CallbackModel((_, _) => new(ToolCalls: [new("uncertain", "read_fixture", "{}")])),
            new(TimeoutSeconds: 1), Emit, Approve);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        var paused = (await store.LoadAsync(id))!;
        Assert.Equal("paused", paused.Info.Status);
        Assert.Equal("time_limit", paused.RunCheckpoint!.StopReason);
        Assert.Contains(paused.Messages, message => message.Role == "tool" && message.Content.Contains("may or may not"));
        await engine.ContinueAsync(id, new CallbackModel((messages, _) =>
        {
            Assert.Contains(messages, message => message.Content.Contains("do not blindly replay"));
            return new(TextDelta: "Review the workspace before deciding next actions.");
        }), new(), Emit, Approve);
        Assert.Equal(1, tool.Calls);

        var cancelledId = await Create(store);
        using var cancelled = new CancellationTokenSource();
        var cancelModel = new BlockingModel();
        var cancelRun = engine.RunAsync(cancelledId, "Inspect", cancelModel, new(), Emit, Approve, cancelled.Token);
        await cancelModel.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await cancelRun.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", (await store.LoadAsync(cancelledId))!.Info.Status);
    }

    [Fact]
    public async Task DurableSteeringWatermarkHandlesCrashBeforeAndAfterMessageCommit()
    {
        using var store = new SqliteSessionStore(Database);
        var id = await Create(store, [new("user", "Original request")]);
        var initial = (await store.LoadAsync(id))!;
        var queued = await store.AppendEventAsync(new(id, "old-run", "input.queued", "Do not publish anything."));
        var checkpoint = new RunCheckpoint(1, "old-run", 0, new(), 0, DateTimeOffset.UtcNow);
        await store.SaveAsync(initial with { Info = initial.Info with { Status = "interrupted" }, RunCheckpoint = checkpoint });
        var view = (await store.ReadViewAsync(id))!;
        Assert.Equal(queued.Sequence, Assert.Single(view.PendingInputs!.Items).Sequence);
        Assert.False(view.PendingInputs.LegacyRecovery);
        await new AgentEngine(store, []).ContinueAsync(id, new CallbackModel((messages, _) =>
        {
            Assert.Single(messages, message => message.Content == "Do not publish anything.");
            return new(TextDelta: "Only report progress.");
        }), new(), Emit, Approve);
        Assert.Empty((await store.PendingInputsAsync(id)).Items);
        var applied = (await store.LoadAsync(id))!;
        // A snapshot was committed, but input.steered publication was lost: watermark remains authoritative.
        var second = await store.AppendEventAsync(new(id, "another-run", "input.queued", "Do not publish anything."));
        await store.SaveAsync(applied with { Messages = applied.Messages.Append(new("user", "Do not publish anything.")).ToArray(),
            RunCheckpoint = applied.RunCheckpoint! with { LastAppliedInputSequence = second.Sequence } });
        Assert.Empty((await store.PendingInputsAsync(id)).Items);
    }

    [Fact]
    public async Task LegacySteeringCrashAfterMessageSaveRequiresReviewWithoutDuplicatingInstructions()
    {
        using var store = new SqliteSessionStore(Database);
        const string recovered = "Do not publish anything.";
        var id = await Create(store, [new("user", "Original task"), new("user", recovered)]);
        var initial = (await store.LoadAsync(id))!;
        await store.SaveAsync(initial with { Info = initial.Info with { Status = "interrupted" } });
        // Legacy code saved the applied user message, then crashed before publishing input.steered.
        await store.AppendEventAsync(new(id, "legacy-run", "input.queued", recovered));
        var snapshot = JsonSerializer.Serialize(await store.LoadAsync(id), Wire.Json);
        var events = JsonSerializer.Serialize(await store.EventsAsync(id), Wire.Json);
        var pending = (await store.ReadViewAsync(id))!.PendingInputs!;
        Assert.True(pending.LegacyRecovery);
        Assert.Equal(recovered, Assert.Single(pending.Items).Text);
        var modelCalled = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new AgentEngine(store, []).RunAsync(id,
            "Continue the task", new CallbackModel((_, _) =>
            {
                modelCalled = true;
                return new(TextDelta: "Must not run before review.");
            }), new(), Emit, Approve));
        Assert.Contains("Review the recovered instructions and start a new chat", error.Message);
        Assert.False(modelCalled);
        Assert.Equal(snapshot, JsonSerializer.Serialize(await store.LoadAsync(id), Wire.Json));
        Assert.Equal(events, JsonSerializer.Serialize(await store.EventsAsync(id), Wire.Json));
        Assert.Single((await store.LoadAsync(id))!.Messages, message => message.Content == recovered);
    }

    [Fact]
    public async Task SqlitePagesCoverLongHistoryWithoutGapsAndKeepToolGroupsTogether()
    {
        using var store = new SqliteSessionStore(Database);
        List<ChatMessage> messages = [];
        for (var index = 0; index < 2000; index++)
        {
            messages.Add(new("user", "request " + index));
            messages.Add(new("assistant", "", [new("call-" + index, "read_fixture", "{}")]));
            messages.Add(new("tool", "result " + index, ToolCallId: "call-" + index));
        }
        var id = await Create(store, messages);
        for (var index = 0; index < 501; index++) await store.AppendEventAsync(new(id, "run", "fixture", index.ToString()));
        var view = (await store.ReadViewAsync(id, 100))!;
        Assert.Equal(6000, view.TotalMessages);
        Assert.InRange(view.Messages.Count, 100, 101);
        Assert.NotEqual("tool", view.Messages[0].Role);
        var loaded = new List<ChatMessage>(view.Messages);
        var cursor = view.FirstMessageIndex;
        while (cursor > 0)
        {
            var page = (await store.MessagesPageAsync(id, cursor, 137))!;
            Assert.True(page.Available);
            Assert.NotEqual("tool", page.Messages[0].Role);
            Assert.Equal(cursor - page.FirstMessageIndex, page.Messages.Count);
            loaded.InsertRange(0, page.Messages);
            cursor = page.FirstMessageIndex;
        }
        Assert.Equal(messages.Select(message => message.Content), loaded.Select(message => message.Content));
        var events = new List<AgentEvent>();
        long? before = null;
        do
        {
            var page = await store.EventsPageAsync(id, before, 73);
            events.InsertRange(0, page.Items);
            before = page.HasMore ? page.NextBeforeSequence : null;
        } while (before is not null);
        Assert.Equal(Enumerable.Range(1, 501).Select(index => (long)index), events.Select(evt => evt.Sequence));
        Assert.Equal(events[149], await store.GetEventAsync(id, 150));
        Assert.Null(await store.GetEventAsync(id, 9999));
        await Assert.ThrowsAsync<ArgumentException>(() => store.MessagesPageAsync(id, 2));
    }

    [Fact]
    public async Task DisplayPagesOmitImageBytesAndNativeStateWhileSourceAndUnknownOutcomeRemainExact()
    {
        using var store = new SqliteSessionStore(Database);
        var image = new ImageAttachment("image/png", new string('A', 2_000_000), "fixture.png");
        var native = new ModelContinuationState("fixture", "https://fixture.invalid", "fixture", Wire.Element(new { secret = new string('s', 2_000_000) }));
        var id = await Create(store, [new("user", "inspect", Images: [image]), new("assistant", "observed", Continuation: native)]);
        var view = (await store.ReadViewAsync(id))!;
        Assert.True(view.Available);
        Assert.True(view.OmittedPayloads);
        Assert.Equal("", Assert.Single(view.Messages[0].Images!).Base64Data);
        Assert.Null(view.Messages[1].Continuation);
        var original = (await store.LoadAsync(id))!;
        Assert.Equal(image.Base64Data, original.Messages[0].Images![0].Base64Data);
        Assert.NotNull(original.Messages[1].Continuation);
        Assert.Single(await store.ListAsync());
    }

    [Fact]
    public async Task OversizedGroupAndEventNeverAdvanceTheCursorOrPretendHistoryIsComplete()
    {
        using var store = new SqliteSessionStore(Database);
        var id = await Create(store, [new("user", new string('x', SessionHistoryPages.MaximumPageCharacters + 1))]);
        var page = (await store.MessagesPageAsync(id))!;
        Assert.False(page.Available);
        Assert.True(page.HasEarlierMessages);
        Assert.Equal(1, page.FirstMessageIndex);
        Assert.Empty(page.Messages);
        var source = await store.AppendEventAsync(new(id, "run", "huge", new string('x', SessionHistoryPages.MaximumPageCharacters + 1)));
        var events = await store.EventsPageAsync(id);
        Assert.False(events.Available);
        Assert.True(events.HasMore);
        Assert.Null(events.NextBeforeSequence);
        Assert.Equal(source, await store.GetEventAsync(id, source.Sequence));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadViewAsync(id, cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task EventStreamingReleasesTheGateAndUsesAFixedHighWatermarkAcrossBatches()
    {
        using var store = new SqliteSessionStore(Database);
        var id = await Create(store);
        for (var index = 0; index < 205; index++)
            await store.AppendEventAsync(new(id, "run", index % 2 == 0 ? "even" : "odd", index.ToString()));
        List<long> observed = [];
        await foreach (var evt in store.StreamEventsAsync(id))
        {
            observed.Add(evt.Sequence);
            if (observed.Count == 1)
            {
                // Consumer-side work may read or append with the same store; a yielded reader must not own its gate.
                Assert.NotNull(await store.GetEventAsync(id, evt.Sequence).WaitAsync(TimeSpan.FromSeconds(5)));
                await store.AppendEventAsync(new(id, "run", "even", "appended after snapshot")).WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        Assert.Equal(Enumerable.Range(1, 205).Select(index => (long)index), observed);
        List<long> reverse = [];
        await foreach (var evt in store.StreamEventsAsync(id, descending: true, eventType: "odd")) reverse.Add(evt.Sequence);
        Assert.Equal(Enumerable.Range(1, 102).Select(index => (long)(index * 2)).Reverse(), reverse);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10001, 1)]
    [InlineData(1, 604801)]
    public void SharedRunLimitsRejectInvalidValues(int iterations, int seconds) =>
        Assert.Throws<ArgumentException>(() => RunLimits.Validate(new(MaxIterations: iterations, TimeoutSeconds: seconds)));

    [Fact]
    public void SharedRunLimitsAcceptGenerousDefaultsAndExplicitBoundedLongRuns()
    {
        Assert.Equal(256, new RunOptions().MaxIterations);
        Assert.Equal(21600, new RunOptions().TimeoutSeconds);
        RunLimits.Validate(new(MaxIterations: 10_000, TimeoutSeconds: 604_800, MaxReportedTokens: 12345));
    }

    [Fact]
    public void ContinuationValidationRejectsMalformedProgressAndCompletedSessions()
    {
        var now = DateTimeOffset.UtcNow;
        var checkpoint = new RunCheckpoint(1, "run", 0, new(), 1, now);
        var session = new AgentSession(new("session", "Task", _directory, now, now, "paused"), [new("user", "Keep the original task")],
            RunCheckpoint: checkpoint);
        RunLimits.ValidateContinuation(session);
        RunCheckpoint[] invalid = [checkpoint with { Version = 2 }, checkpoint with { Options = null! },
            checkpoint with { Options = new(MaxIterations: 0) }, checkpoint with { ActiveStartMessageIndex = -1 },
            checkpoint with { ActiveStartMessageIndex = 1 }, checkpoint with { LastAppliedInputSequence = -1 },
            checkpoint with { IterationsCompleted = 257 }, checkpoint with { Usage = new(-1) },
            checkpoint with { Usage = new(long.MaxValue, 1) }, checkpoint with { Usage = new(ModelCalls: 1, IncompleteCalls: 2) }];
        foreach (var malformed in invalid)
            Assert.Throws<InvalidOperationException>(() => RunLimits.ValidateContinuation(session with { RunCheckpoint = malformed }));
        Assert.Throws<InvalidOperationException>(() => RunLimits.ValidateContinuation(session with { Info = session.Info with { Status = "completed" } }));
        Assert.Throws<InvalidOperationException>(() => RunLimits.ValidateContinuation(session with { Messages = [new("assistant", "Not an original instruction")] }));
    }

    private sealed class CountTool(Func<CancellationToken, Task>? beforeResult = null) : IAgentTool
    {
        public int Calls { get; private set; }
        public ToolDefinition Definition => new("read_fixture", "Read a deterministic fixture.", "{\"type\":\"object\"}");
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (beforeResult is not null) await beforeResult(cancellationToken);
            return new("observed");
        }
    }

    private sealed class CallbackModel(Func<IReadOnlyList<ChatMessage>, int, ModelUpdate> callback) : IAgentModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return callback(messages, ++_calls);
        }
    }

    private sealed class FaultModel : IAgentModel
    {
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new(ToolCalls: [new("partial", "read_fixture", "{}")], InputTokens: 20, OutputTokens: 5,
                Continuation: new("fixture", "https://fixture.invalid", "fixture", Wire.Element(new { privateState = "discard" })));
            throw new IOException("Fixture stream interrupted.");
        }
    }

    private sealed class SummaryUsageModel : IAgentModel
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            Assert.Empty(tools);
            Assert.Contains("summarize", messages[0].Content);
            await Task.Yield();
            yield return new(TextDelta: "## Goal\nRetain API.\n## Progress\nPending.\n## Next Steps\nReview.", InputTokens: 10, OutputTokens: 2);
            yield return new(InputTokens: 15, OutputTokens: 5);
        }
    }

    private sealed class BlockingModel : IAgentModel
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield return new(TextDelta: "unreachable");
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
