using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class AdditionalToolSetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-additional-tools-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteSessionStore _store;

    public AdditionalToolSetTests()
    {
        Directory.CreateDirectory(_root);
        _store = new(Path.Combine(_root, "sessions.db"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalToolUsesExistingApprovalLoopAndPersistsTheObservedResult(bool approved)
    {
        var id = await Session();
        var external = new ExternalTool("integration_fixture", "external result");
        var set = new TrackedToolSet(external);
        var factoryCalls = 0;
        var engine = new AgentEngine(_store, [], additionalToolsFactory: (workspace, _, token) =>
        {
            Assert.Equal(_root, workspace);
            Assert.True(token.CanBeCanceled);
            factoryCalls++;
            return Task.FromResult<IAgentToolSet?>(set);
        });
        var approvals = new List<ApprovalRequest>();
        var model = new ToolThenTextModel(external.Definition.Name);
        await engine.RunAsync(id, "Check the external fixture.", model, new(RunId: "external-run"), NoOutput,
            (request, _) => { approvals.Add(request); return Task.FromResult(approved); });

        Assert.Equal(1, factoryCalls);
        Assert.Equal(approved ? 1 : 0, external.Calls);
        Assert.Equal(1, set.DisposeCalls);
        var approval = Assert.Single(approvals);
        Assert.Equal(external.Definition.Name, approval.ToolName);
        Assert.Equal(id, approval.SessionId);
        Assert.Equal("external-run", approval.RunId);
        Assert.All(model.Definitions, definitions => Assert.Contains(definitions, definition => definition.Name == external.Definition.Name));
        var saved = (await _store.LoadAsync(id))!;
        Assert.Equal("completed", saved.Info.Status);
        var observation = Assert.Single(saved.Messages, message => message.Role == "tool");
        Assert.Equal("integration-call", observation.ToolCallId);
        Assert.Equal(!approved, observation.ToolIsError);
        if (approved)
        {
            Assert.Equal("external result", observation.Content);
            var context = Assert.Single(external.Contexts);
            Assert.Equal(id, context.SessionId);
            Assert.Equal("external-run", context.RunId);
        }
        else Assert.Contains("declined", observation.Content);
        var events = await _store.EventsAsync(id);
        Assert.Contains(events, evt => evt.Type == "approval.requested" && evt.ApprovalId == approval.Id);
        var completed = Assert.Single(events, evt => evt.Type == "tool.completed");
        Assert.Equal(!approved, completed.Result!.IsError);
        Assert.Equal(observation.Content, completed.Result.Content);
        Assert.Equal("run.completed", events[^1].Type);
    }

    [Fact]
    public async Task ReadOnlyNeverInvokesTheExternalFactory()
    {
        var id = await Session();
        var called = false;
        var model = new TextModel((_, definitions) => Assert.DoesNotContain(definitions, definition => definition.Name == "integration_fixture"));
        var engine = new AgentEngine(_store, [], additionalToolsFactory: (_, _, _) =>
        {
            called = true;
            throw new InvalidOperationException("Read-only must not initialize external clients or launch an integration process.");
        });
        await engine.RunAsync(id, "Explain only.", model, new(ExecutionMode: ExecutionMode.ReadOnly), NoOutput,
            (_, _) => throw new InvalidOperationException("Read-only fixture requested approval."));
        Assert.False(called);
        Assert.Equal("completed", (await _store.LoadAsync(id))!.Info.Status);
    }

    [Fact]
    public async Task CancellationDuringExternalExecutionDisposesTheSetAndPreservesUnknownOutcome()
    {
        var id = await Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var external = new ExternalTool("integration_fixture", "unobserved", async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        var set = new TrackedToolSet(external);
        var engine = new AgentEngine(_store, [], additionalToolsFactory: (_, _, _) => Task.FromResult<IAgentToolSet?>(set));
        using var cancellation = new CancellationTokenSource();
        var run = engine.RunAsync(id, "Execute fixture.", new ToolThenTextModel(external.Definition.Name), new(), NoOutput, Approve, cancellation.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { cancellation.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(1, external.Calls);
        Assert.Equal(1, set.DisposeCalls);
        var saved = (await _store.LoadAsync(id))!;
        Assert.Equal("cancelled", saved.Info.Status);
        var observation = Assert.Single(saved.Messages, message => message.Role == "tool");
        Assert.True(observation.ToolIsError);
        Assert.Contains("may or may not", observation.Content);
        Assert.DoesNotContain(saved.Messages, message => message.Role == "tool" && message.Content == "unobserved");
        Assert.Equal("run.cancelled", (await _store.EventsAsync(id))[^1].Type);
    }

    [Fact]
    public async Task CancellationWhileApprovalIsPendingDisposesWithoutExecuting()
    {
        var id = await Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var external = new ExternalTool("integration_fixture", "must not execute");
        var set = new TrackedToolSet(external);
        var engine = new AgentEngine(_store, [], additionalToolsFactory: (_, _, _) => Task.FromResult<IAgentToolSet?>(set));
        using var cancellation = new CancellationTokenSource();
        var run = engine.RunAsync(id, "Execute fixture.", new ToolThenTextModel(external.Definition.Name), new(), NoOutput,
            async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            }, cancellation.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { cancellation.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(0, external.Calls);
        Assert.Equal(1, set.DisposeCalls);
        Assert.Equal("cancelled", (await _store.LoadAsync(id))!.Info.Status);
    }

    [Fact]
    public async Task BrokenModelStreamDisposesWithoutExecutingItsPartialToolCall()
    {
        var id = await Session();
        var external = new ExternalTool("integration_fixture", "must not execute");
        var set = new TrackedToolSet(external);
        var engine = new AgentEngine(_store, [], additionalToolsFactory: (_, _, _) => Task.FromResult<IAgentToolSet?>(set));
        await engine.RunAsync(id, "Check fixture.", new BrokenModel(external.Definition.Name), new(), NoOutput, Approve);
        Assert.Equal(0, external.Calls);
        Assert.Equal(1, set.DisposeCalls);
        var saved = (await _store.LoadAsync(id))!;
        Assert.Equal("failed", saved.Info.Status);
        Assert.DoesNotContain(saved.Messages, message => message.ToolCalls is { Count: > 0 });
        Assert.Equal("run.failed", (await _store.EventsAsync(id))[^1].Type);
    }

    [Fact]
    public async Task TerminalCompletionWaitsForExternalClientCleanup()
    {
        var id = await Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var set = new TrackedToolSet()
        {
            OnDispose = async () =>
            {
                entered.TrySetResult();
                await release.Task;
            }
        };
        var engine = new AgentEngine(_store, [], additionalToolsFactory: (_, _, _) => Task.FromResult<IAgentToolSet?>(set));
        var run = engine.RunAsync(id, "Report only.", new TextModel(), new(), NoOutput, Approve);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
            Assert.Equal("running", (await _store.LoadAsync(id))!.Info.Status);
            Assert.DoesNotContain(await _store.EventsAsync(id), evt => evt.Type is "run.completed" or "run.failed" or "run.cancelled");
        }
        finally { release.TrySetResult(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.Equal(1, set.DisposeCalls);
        Assert.Equal("completed", (await _store.LoadAsync(id))!.Info.Status);
        Assert.Equal("run.completed", (await _store.EventsAsync(id))[^1].Type);
    }

    [Fact]
    public async Task ExternalCleanupFailurePersistsFailureAndNeverPublishesCompleted()
    {
        var id = await Session();
        const string privateDetail = "FIXTURE_PRIVATE_CLEANUP_DETAIL";
        var set = new TrackedToolSet() { OnDispose = () => throw new IOException(privateDetail) };
        var engine = new AgentEngine(_store, [], additionalToolsFactory: (_, _, _) => Task.FromResult<IAgentToolSet?>(set));
        await engine.RunAsync(id, "Report only.", new TextModel(), new(), NoOutput, Approve);
        Assert.Equal(1, set.DisposeCalls);
        Assert.Equal("failed", (await _store.LoadAsync(id))!.Info.Status);
        var events = await _store.EventsAsync(id);
        var cleanup = Assert.Single(events, evt => evt.Type == "integrations.cleanup_failed");
        Assert.Contains("unknown", cleanup.Text);
        Assert.Equal("run.failed", events[^1].Type);
        Assert.True(cleanup.Sequence < events[^1].Sequence);
        Assert.DoesNotContain(events, evt => evt.Type == "run.completed");
        Assert.DoesNotContain(events, evt => evt.Text?.Contains(privateDetail, StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("inspect_evidence")]
    [InlineData("read_skill")]
    [InlineData("remember_experience")]
    [InlineData("recall_project_memory")]
    [InlineData("existing_tool")]
    public async Task ReservedOrExistingNamesRejectTheSetAndStillDisposeIt(string name)
    {
        var id = await Session();
        var external = new ExternalTool(name, "must not execute");
        var set = new TrackedToolSet(external);
        var model = new TextModel();
        var factoryCalls = 0;
        var engine = new AgentEngine(_store, [new ExternalTool("existing_tool", "base")], additionalToolsFactory: (_, _, _) =>
        {
            factoryCalls++;
            return Task.FromResult<IAgentToolSet?>(factoryCalls == 1 ? set : null);
        });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(id, "Check fixture.", model, new(), NoOutput, Approve));
        Assert.Contains("conflicts", failure.Message);
        Assert.Equal(1, set.DisposeCalls);
        Assert.Equal(0, external.Calls);
        Assert.Equal(0, model.Requests);
        // The per-session run lock must also be released on setup failure.
        await engine.RunAsync(id, "Report only.", model, new(), NoOutput, Approve);
        Assert.Equal("completed", (await _store.LoadAsync(id))!.Info.Status);
    }

    [Fact]
    public async Task DuplicateNamesWithinAnExternalSetRejectAndDisposeTheEntireSet()
    {
        var id = await Session();
        var first = new ExternalTool("duplicate", "first");
        var second = new ExternalTool("duplicate", "second");
        var set = new TrackedToolSet(first, second);
        var engine = new AgentEngine(_store, [], additionalToolsFactory: (_, _, _) => Task.FromResult<IAgentToolSet?>(set));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(id, "Check fixture.", new TextModel(), new(), NoOutput, Approve));
        Assert.Equal(1, set.DisposeCalls);
        Assert.Equal(0, first.Calls + second.Calls);
    }

    [Fact]
    public async Task EveryRunGetsFreshClientsAndReceivesTheConfiguredSecretEnvironmentNames()
    {
        var firstSession = await Session();
        var otherSession = await Session();
        string[] secrets = ["FIXTURE_PROVIDER_TOKEN", "FIXTURE_PRIVATE_CREDENTIAL"];
        List<TrackedToolSet> sets = [];
        List<ExternalTool> clients = [];
        List<IReadOnlyList<string>?> exclusions = [];
        var engine = new AgentEngine(_store, [], additionalToolsFactory: (workspace, excluded, token) =>
        {
            Assert.Equal(_root, workspace);
            Assert.True(token.CanBeCanceled);
            exclusions.Add(excluded);
            var client = new ExternalTool("integration_fixture", "client-" + clients.Count);
            clients.Add(client);
            var set = new TrackedToolSet(client);
            sets.Add(set);
            return Task.FromResult<IAgentToolSet?>(set);
        });
        var sessions = new[] { firstSession, firstSession, otherSession };
        for (var index = 0; index < sessions.Length; index++)
        {
            var model = new ToolThenTextModel("integration_fixture");
            var runId = "fresh-" + index;
            await engine.RunAsync(sessions[index], "Check the current client.", model, new(RunId: runId, SecretEnvironmentVariables: secrets), NoOutput, Approve);
            var context = Assert.Single(clients[index].Contexts);
            Assert.Equal(sessions[index], context.SessionId);
            Assert.Equal(runId, context.RunId);
            Assert.Equal(secrets, context.ExcludedEnvironmentVariables);
            Assert.Equal(1, sets[index].DisposeCalls);
            Assert.Contains(model.LastMessages!, message => message.Role == "tool" && message.Content == "client-" + index);
        }
        Assert.Equal(3, sets.Count);
        Assert.All(sets, set => Assert.Equal(1, set.DisposeCalls));
        Assert.All(clients, client => Assert.Equal(1, client.Calls));
        Assert.All(exclusions, excluded => Assert.Equal(secrets, excluded));
        Assert.Equal(3, clients.Distinct().Count());
        Assert.Equal(3, sets.Distinct().Count());
        var secondHistory = (await _store.LoadAsync(otherSession))!.Messages;
        Assert.DoesNotContain(secondHistory, message => message.Content is "client-0" or "client-1");
        Assert.Contains(secondHistory, message => message.Role == "tool" && message.Content == "client-2");
    }

    [Fact]
    public async Task ConcurrentSessionsDisposeOnlyTheirOwnClientSet()
    {
        var firstSession = await Session();
        var secondSession = await Session();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstClient = new ExternalTool("integration_fixture", "first", async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        var secondClient = new ExternalTool("integration_fixture", "second");
        var firstSet = new TrackedToolSet(firstClient);
        var secondSet = new TrackedToolSet(secondClient);
        var factoryCalls = 0;
        var engine = new AgentEngine(_store, [], additionalToolsFactory: (_, _, _) =>
            Task.FromResult<IAgentToolSet?>(Interlocked.Increment(ref factoryCalls) == 1 ? firstSet : secondSet));
        using var cancellation = new CancellationTokenSource();
        var first = engine.RunAsync(firstSession, "First client.", new ToolThenTextModel("integration_fixture"), new(RunId: "first"), NoOutput, Approve, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await engine.RunAsync(secondSession, "Second client.", new ToolThenTextModel("integration_fixture"), new(RunId: "second"), NoOutput, Approve)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, firstSet.DisposeCalls);
            Assert.Equal(1, secondSet.DisposeCalls);
            Assert.Equal(firstSession, Assert.Single(firstClient.Contexts).SessionId);
            Assert.Equal(secondSession, Assert.Single(secondClient.Contexts).SessionId);
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, firstSet.DisposeCalls);
        }
        finally
        {
            release.TrySetResult();
            cancellation.Cancel();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(1, firstClient.Calls);
        Assert.Equal(1, secondClient.Calls);
    }

    private async Task<string> Session()
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await _store.SaveAsync(new(new(id, "additional tool fixture", _root, now, now), []));
        return id;
    }

    private static ValueTask NoOutput(AgentEvent _, CancellationToken __) => ValueTask.CompletedTask;
    private static Task<bool> Approve(ApprovalRequest _, CancellationToken __) => Task.FromResult(true);

    private sealed class TrackedToolSet(params IAgentTool[] tools) : IAgentToolSet
    {
        public IReadOnlyList<IAgentTool> Tools { get; } = tools;
        public int DisposeCalls { get; private set; }
        public Func<ValueTask>? OnDispose { get; init; }
        public ValueTask DisposeAsync() { DisposeCalls++; return OnDispose?.Invoke() ?? ValueTask.CompletedTask; }
    }

    private sealed class ExternalTool(string name, string result, Func<CancellationToken, Task>? execute = null) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(name, "External client fixture", "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}", RequiresApproval: true);
        public int Calls { get; private set; }
        public List<ToolExecutionContext> Contexts { get; } = [];
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            Contexts.Add(context);
            if (execute is not null) await execute(cancellationToken);
            return new(result);
        }
    }

    private sealed class ToolThenTextModel(string name) : IAgentModel
    {
        private int _requests;
        public List<IReadOnlyList<ToolDefinition>> Definitions { get; } = [];
        public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Definitions.Add(tools.ToArray());
            LastMessages = messages.ToArray();
            await Task.Yield();
            yield return ++_requests == 1 ? new(ToolCalls: [new("integration-call", name, "{}")]) : new(TextDelta: "Observed the fixture result.");
        }
    }

    private sealed class TextModel(Action<IReadOnlyList<ChatMessage>, IReadOnlyList<ToolDefinition>>? inspect = null) : IAgentModel
    {
        public int Requests { get; private set; }
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            inspect?.Invoke(messages, tools);
            await Task.Yield();
            yield return new(TextDelta: "Fixture report.");
        }
    }

    private sealed class BrokenModel(string name) : IAgentModel
    {
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new(ToolCalls: [new("partial-call", name, "{}")]);
            throw new IOException("External fixture model stream was interrupted.");
        }
    }

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        var full = Path.GetFullPath(_root);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(parent, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            !Path.GetFileName(full).StartsWith("cogitide-additional-tools-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
        Directory.Delete(full, true);
    }
}
