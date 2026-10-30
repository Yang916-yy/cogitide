using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class SpeculationTests : IDisposable
{
    private const string SourcePath = "src/sample.txt";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogito-speculation-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceService _workspace = new();
    private string SourceFile => Path.Combine(_root, SourcePath);
    private ToolExecutionContext Owner => new(_root, SessionId: "fixture-chat", RunId: "fixture-run");
    private IAgentTool NativeRead => WorkspaceTools.Create(_workspace).Single(tool => tool.Definition.Name == "read_file");
    private static RunOptions Enabled => new(AutoCompact: false, Research: new(ReadOnlyPrefetch: true));

    public SpeculationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(SourceFile, "needle\n" + new string('x', 20000) + "\nTAIL-A", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_root, "src/other.txt"), "other", new UTF8Encoding(false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EngineOverlapsNextModelRequestThenUsesHitOrRereadsFullFileMutation(bool mutateAfterPrediction)
    {
        File.WriteAllText(Path.Combine(_root, "src/AGENTS.md"), "SCOPE_HINT: preserve the tail.");
        var release = Signal();
        var read = new ObservedRead(NativeRead, (ordinal, ct) => ordinal == 1 ? release.Task.WaitAsync(ct) : Task.CompletedTask);
        using var store = new SqliteSessionStore(Path.Combine(_root, "session.db"));
        var session = await CreateSession(store);
        var engine = new AgentEngine(store, WithRead(read), WorkspaceService.Resolve);
        var events = new List<AgentEvent>();
        var expected = FileHash();
        var overlapObserved = false;
        var model = new Model(async (step, messages, ct) =>
        {
            if (step == 1) return new(ToolCalls: [Search()]);
            if (step == 2)
            {
                await read.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                overlapObserved = !read.FirstCompleted.Task.IsCompleted;
                Assert.Contains("SCOPE_HINT", messages[0].Content);
                release.TrySetResult();
                await read.FirstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                if (mutateAfterPrediction)
                {
                    var text = await File.ReadAllTextAsync(SourceFile, ct);
                    await File.WriteAllTextAsync(SourceFile, text.Replace("TAIL-A", "TAIL-B", StringComparison.Ordinal), new UTF8Encoding(false), ct);
                    expected = FileHash();
                }
                // Explicit defaults must match the equivalent omitted-default prediction.
                return new(ToolCalls: [Read(new { path = SourcePath, offset = 0, limit = 200, characterOffset = 0 })]);
            }
            using var result = JsonDocument.Parse(messages.Last().Content);
            Assert.Equal(expected, result.RootElement.GetProperty("sha256").GetString());
            return new(TextDelta: "Current file checked.");
        });
        try
        {
            await engine.RunAsync(session.Info.Id, "Inspect the needle.", model, Enabled,
                (evt, _) => { events.Add(evt); return ValueTask.CompletedTask; }, (_, _) => Task.FromResult(false));
        }
        finally { release.TrySetResult(); }
        Assert.True(overlapObserved);
        Assert.Equal("run.completed", events.Last().Type);
        Assert.Equal(mutateAfterPrediction ? 2 : 1, read.Calls);
        Assert.Equal(mutateAfterPrediction ? 0 : 1, events.Count(evt => evt.Type == "speculation.hit"));
        Assert.Single(events, evt => evt.Type == "tool.completed" && evt.Call!.Name == "read_file");
        using (var summary = JsonDocument.Parse(events.Single(evt => evt.Type == "speculation.summary").Text!))
            Assert.Equal(mutateAfterPrediction ? 1 : 0, summary.RootElement.GetProperty("stale").GetInt32());

        // A later run in the same chat cannot consume a previous run's prediction or cache.
        var beforeSecondRun = read.Calls;
        events.Clear();
        await engine.RunAsync(session.Info.Id, "Read it again.", new Model((step, _, _) => Task.FromResult(step == 1
            ? new ModelUpdate(ToolCalls: [Read(new { path = SourcePath })]) : new(TextDelta: "Read again."))), Enabled,
            (evt, _) => { events.Add(evt); return ValueTask.CompletedTask; }, (_, _) => Task.FromResult(false));
        Assert.Equal(beforeSecondRun + 1, read.Calls);
        Assert.DoesNotContain(events, evt => evt.Type is "speculation.hit" or "speculation.started");
        Assert.Equal("run.completed", events.Last().Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnconsumedPredictionCannotSurviveRunEndAndDisabledModeDoesNoExtraRead(bool enabled)
    {
        var read = new ObservedRead(NativeRead);
        using var store = new SqliteSessionStore(Path.Combine(_root, "session.db"));
        var session = await CreateSession(store);
        var engine = new AgentEngine(store, WithRead(read), WorkspaceService.Resolve);
        var events = new List<AgentEvent>();
        var options = enabled ? Enabled : new RunOptions(AutoCompact: false);
        await engine.RunAsync(session.Info.Id, "Only locate the file.", new Model(async (step, _, ct) =>
        {
            if (step == 1) return new(ToolCalls: [Search()]);
            if (enabled) await read.FirstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return new(TextDelta: "Location found; no requested read.");
        }), options, (evt, _) => { events.Add(evt); return ValueTask.CompletedTask; }, (_, _) => Task.FromResult(false));
        Assert.Equal("run.completed", events.Last().Type);
        Assert.Equal(enabled ? 1 : 0, read.Calls);
        Assert.DoesNotContain(events, evt => evt.Type == "speculation.hit" || evt.Type == "tool.completed" && evt.Call!.Name == "read_file");
        events.Clear();
        await engine.RunAsync(session.Info.Id, "Now read the file.", new Model((step, _, _) => Task.FromResult(step == 1
            ? new ModelUpdate(ToolCalls: [Read(new { path = SourcePath })]) : new(TextDelta: "Read completed."))), Enabled,
            (evt, _) => { events.Add(evt); return ValueTask.CompletedTask; }, (_, _) => Task.FromResult(false));
        Assert.Equal("run.completed", events.Last().Type);
        Assert.Equal(enabled ? 2 : 1, read.Calls);
        Assert.DoesNotContain(events, evt => evt.Type == "speculation.hit");
    }

    [Theory]
    [InlineData("{\"path\":\"src/sample.txt\",\"offset\":1}")]
    [InlineData("{\"path\":\"src/sample.txt\",\"limit\":1}")]
    [InlineData("{\"path\":\"src/sample.txt\",\"characterOffset\":1}")]
    [InlineData("{\"path\":\"src/other.txt\"}")]
    [InlineData("{\"path\":\"src/sample.txt\",\"unknown\":true}")]
    public async Task DifferentRequestsNeverConsumePrediction(string arguments)
    {
        await using var prefetch = new ReadOnlyPrefetch(_root, WorkspaceService.Resolve, 2);
        Assert.True(await prefetch.ScheduleAsync(Read(new { path = SourcePath }), NativeRead, Owner, CancellationToken.None));
        Assert.Null(await prefetch.TryTakeAsync(new("actual", "read_file", arguments), CancellationToken.None));
        Assert.Equal(1, prefetch.Statistics.Mismatches);
        Assert.Equal(0, prefetch.Statistics.Hits);
        Assert.Null(await prefetch.TryTakeAsync(Read(new { path = SourcePath }), CancellationToken.None));
    }

    [Fact]
    public async Task ExpectedHashMismatchCannotBeHiddenByAValidCachedRead()
    {
        await using var prefetch = new ReadOnlyPrefetch(_root, WorkspaceService.Resolve, 1);
        Assert.True(await prefetch.ScheduleAsync(Read(new { path = SourcePath }), NativeRead, Owner, CancellationToken.None));
        var actual = Read(new { path = SourcePath, expectedSha256 = new string('0', 64) });
        Assert.Null(await prefetch.TryTakeAsync(actual, CancellationToken.None));
        Assert.Equal(1, prefetch.Statistics.Stale);
        Assert.True((await NativeRead.ExecuteAsync(actual, Owner)).IsError);
    }

    [Fact]
    public async Task MatchingPredictionIsSingleUseAndHardBudgetStopsFurtherLaunches()
    {
        await using var prefetch = new ReadOnlyPrefetch(_root, WorkspaceService.Resolve, 1);
        var call = Read(new { path = SourcePath });
        Assert.True(await prefetch.ScheduleAsync(call, NativeRead, Owner, CancellationToken.None));
        Assert.NotNull(await prefetch.TryTakeAsync(call, CancellationToken.None));
        Assert.Null(await prefetch.TryTakeAsync(call, CancellationToken.None));
        Assert.False(await prefetch.ScheduleAsync(call, NativeRead, Owner, CancellationToken.None));
        Assert.Equal(1, prefetch.Statistics.Started);
        Assert.Equal(1, prefetch.Statistics.Hits);
        await using var disabled = new ReadOnlyPrefetch(_root, WorkspaceService.Resolve, 0);
        Assert.False(await disabled.ScheduleAsync(call, NativeRead, Owner, CancellationToken.None));
        Assert.Equal(0, disabled.Statistics.Started);
    }

    [Fact]
    public async Task ApprovedMutationInvalidatesPredictionBeforeTheNextRead()
    {
        var read = new ObservedRead(NativeRead);
        using var store = new SqliteSessionStore(Path.Combine(_root, "session.db"));
        var session = await CreateSession(store);
        var originalHash = FileHash();
        var events = new List<AgentEvent>();
        var model = new Model(async (step, messages, ct) =>
        {
            if (step == 1) return new(ToolCalls: [Search()]);
            if (step == 2)
            {
                await read.FirstCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return new(ToolCalls: [new("edit", "replace_text", JsonSerializer.Serialize(new
                    { path = SourcePath, oldText = "TAIL-A", newText = "TAIL-B", expectedSha256 = originalHash }))]);
            }
            if (step == 3) return new(ToolCalls: [Read(new { path = SourcePath })]);
            using var data = JsonDocument.Parse(messages.Last().Content);
            Assert.Equal(FileHash(), data.RootElement.GetProperty("sha256").GetString());
            return new(TextDelta: "Edit and read completed.");
        });
        await new AgentEngine(store, WithRead(read), WorkspaceService.Resolve).RunAsync(session.Info.Id, "Change the tail.", model, Enabled,
            (evt, _) => { events.Add(evt); return ValueTask.CompletedTask; }, (_, _) => Task.FromResult(true));
        Assert.Equal("run.completed", events.Last().Type);
        Assert.EndsWith("TAIL-B", File.ReadAllText(SourceFile));
        Assert.Equal(2, read.Calls);
        Assert.DoesNotContain(events, evt => evt.Type == "speculation.hit");
    }

    [Fact]
    public async Task SteeringCancelsPendingPredictionBeforeNewInstructionsAreConsumed()
    {
        var read = new ObservedRead(NativeRead, (ordinal, ct) => ordinal == 1 ? Task.Delay(Timeout.Infinite, ct) : Task.CompletedTask);
        using var store = new SqliteSessionStore(Path.Combine(_root, "session.db"));
        var session = await CreateSession(store);
        var engine = new AgentEngine(store, WithRead(read), WorkspaceService.Resolve);
        var events = new List<AgentEvent>();
        var model = new Model((step, messages, _) =>
        {
            if (step == 1) return Task.FromResult(new ModelUpdate(ToolCalls: [Search()]));
            if (step == 2)
            {
                Assert.True(read.FirstCancelled.Task.IsCompleted);
                Assert.Contains(messages, message => message.Role == "user" && message.Content == "STEERING_NEW_CONSTRAINT");
                return Task.FromResult(new ModelUpdate(ToolCalls: [Read(new { path = SourcePath })]));
            }
            return Task.FromResult(new ModelUpdate(TextDelta: "New constraints observed."));
        });
        await engine.RunAsync(session.Info.Id, "Search first.", model, Enabled, async (evt, _) =>
        {
            events.Add(evt);
            if (evt.Type == "speculation.started")
            {
                await read.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await engine.SteerAsync(session.Info.Id, "STEERING_NEW_CONSTRAINT");
            }
        }, (_, _) => Task.FromResult(false));
        Assert.Equal("run.completed", events.Last().Type);
        Assert.True(read.FirstCancelled.Task.IsCompleted);
        Assert.Equal(2, read.Calls);
        Assert.DoesNotContain(events, evt => evt.Type == "speculation.hit");
    }

    [Fact]
    public async Task CancellationDrainsCancellableReadAndLeavesNoConsumableResult()
    {
        using var cancellation = new CancellationTokenSource();
        var read = new ObservedRead(NativeRead, (_, ct) => Task.Delay(Timeout.Infinite, ct));
        await using var prefetch = new ReadOnlyPrefetch(_root, WorkspaceService.Resolve, 1);
        var call = Read(new { path = SourcePath });
        Assert.True(await prefetch.ScheduleAsync(call, read, Owner, cancellation.Token));
        await read.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        await prefetch.InvalidateAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(read.FirstCancelled.Task.IsCompleted);
        Assert.Null(await prefetch.TryTakeAsync(call, CancellationToken.None));
        Assert.False(read.FirstCompleted.Task.IsCompleted);
    }

    [Fact]
    public async Task NativeReadHonorsCallerCancellationWithoutPublishingPredictedEvidence()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await using var prefetch = new ReadOnlyPrefetch(_root, WorkspaceService.Resolve, 1);
        var call = Read(new { path = SourcePath });
        Assert.True(await prefetch.ScheduleAsync(call, NativeRead, Owner, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prefetch.TryTakeAsync(call, cancellation.Token));
        Assert.Equal(0, prefetch.Statistics.Hits);
        Assert.Null(await prefetch.TryTakeAsync(call, CancellationToken.None));
    }

    [Fact]
    public async Task NonReadToolsAndEscapingPredictionsAreNeverExecuted()
    {
        var forbidden = new NeverExecuteTool(new("run_command", "Must never run speculatively", "{}"));
        var approvalRead = new NeverExecuteTool(new("read_file", "Requires approval", "{}", true));
        await using var prefetch = new ReadOnlyPrefetch(_root, WorkspaceService.Resolve, 4);
        var call = Read(new { path = SourcePath });
        Assert.False(await prefetch.ScheduleAsync(call, forbidden, Owner, CancellationToken.None));
        Assert.False(await prefetch.ScheduleAsync(call, approvalRead, Owner, CancellationToken.None));
        Assert.False(await prefetch.ScheduleAsync(Read(new { path = "../outside" }), NativeRead, Owner, CancellationToken.None));
        Assert.False(await prefetch.ScheduleAsync(new("bad", "read_file", "{\"path\":\"src/sample.txt\",\"sideEffect\":true}"), NativeRead, Owner, CancellationToken.None));
        Assert.Equal(0, forbidden.Calls + approvalRead.Calls);
        Assert.Equal(0, prefetch.Statistics.Started);
        Assert.Null(ReadOnlyPrefetch.Predict(new("write", "write_file", "{}"), new("{\"files\":[\"src/sample.txt\"]}")));
        Assert.Null(ReadOnlyPrefetch.Predict(Search(), new("bad JSON")));
        Assert.Null(ReadOnlyPrefetch.Predict(Search(), new("{\"matches\":[]}")));
    }

    [Fact]
    public async Task HashingRejectsGrowthBeyondBudgetEvenWhenInitialLengthIsSmall()
    {
        using var growing = new GrowingStream();
        Assert.Null(await EvidenceFileHash.ComputeAsync(growing, maximumBytes: 1000));
        Assert.Equal(1001, growing.BytesRead);
        using var exact = new MemoryStream("bounded text"u8.ToArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("bounded text"u8)), await EvidenceFileHash.ComputeAsync(exact, 12));
    }

    private IReadOnlyList<IAgentTool> WithRead(IAgentTool read) => WorkspaceTools.Create(_workspace).Select(tool => tool.Definition.Name == "read_file" ? read : tool).ToArray();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static ToolCall Search() => new("search", "search_files", "{\"query\":\"needle\",\"pathPrefix\":\"src/\"}");
    private static ToolCall Read(object arguments) => new("read", "read_file", JsonSerializer.Serialize(arguments));
    private string FileHash() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(SourceFile)));
    private async Task<AgentSession> CreateSession(ISessionStore store)
    {
        var now = DateTimeOffset.UtcNow;
        var session = new AgentSession(new(Guid.NewGuid().ToString("N"), "speculation fixture", _root, now, now), []);
        await store.SaveAsync(session);
        return session;
    }
    private sealed class ObservedRead(IAgentTool inner, Func<int, CancellationToken, Task>? before = null) : IAgentTool
    {
        private int _calls;
        public int Calls => _calls;
        public TaskCompletionSource FirstStarted { get; } = Signal();
        public TaskCompletionSource FirstCompleted { get; } = Signal();
        public TaskCompletionSource FirstCancelled { get; } = Signal();
        public ToolDefinition Definition => inner.Definition;
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref _calls);
            if (ordinal == 1) FirstStarted.TrySetResult();
            try
            {
                if (before is not null) await before(ordinal, cancellationToken);
                var result = await inner.ExecuteAsync(call, context, cancellationToken);
                if (ordinal == 1) FirstCompleted.TrySetResult();
                return result;
            }
            catch (OperationCanceledException) { if (ordinal == 1) FirstCancelled.TrySetResult(); throw; }
        }
    }
    private sealed class NeverExecuteTool(ToolDefinition definition) : IAgentTool
    {
        public int Calls { get; private set; }
        public ToolDefinition Definition => definition;
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("A forbidden tool was speculatively executed."); }
    }
    private sealed class Model(Func<int, IReadOnlyList<ChatMessage>, CancellationToken, Task<ModelUpdate>> next) : IAgentModel
    {
        private int _calls;
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { yield return await next(++_calls, messages, cancellationToken); }
    }
    private sealed class GrowingStream : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => 1;
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, 97);
            buffer.Span[..count].Fill((byte)'x');
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, true);
    }
}
