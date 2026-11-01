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

/// <summary>Deterministic kernel/broker fixtures; native OS denials are tested separately.</summary>
public sealed class CoreIsolationIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-core-isolation-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteSessionStore _store;
    private string Workspace => Path.Combine(_root, "workspace");

    public CoreIsolationIntegrationTests()
    {
        Directory.CreateDirectory(Workspace);
        _store = new(Path.Combine(_root, "sessions.db"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingRequiredBoundaryFailsBeforeAcceptingInstructions(bool missingBroker)
    {
        var id = await SessionAsync();
        var baseline = new BaselineTool();
        var broker = new FixtureBroker();
        var model = new ScriptedModel();
        var engine = new AgentEngine(_store, [baseline], missingBroker ? WorkspaceService.Resolve : null,
            executionBroker: missingBroker ? null : broker);

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(id, "Inspect the workspace.", model,
            new(RequireOsIsolation: true), Emit, Approve));

        Assert.Empty(model.Requests);
        Assert.Empty(broker.Created);
        Assert.Equal(0, baseline.Calls);
        Assert.Equal("idle", (await _store.LoadAsync(id))!.Info.Status);
        Assert.Empty(await _store.EventsAsync(id));
    }

    [Fact]
    public async Task IsolatedCatalogExcludesHostToolsAndKnowledgeUsesTheExactWorkerOwner()
    {
        var id = await SessionAsync();
        await File.WriteAllTextAsync(Path.Combine(Workspace, "AGENTS.md"), "host copy must not enter model context");
        var skill = Directory.CreateDirectory(Path.Combine(Workspace, ".agents", "skills", "fixture"));
        await File.WriteAllTextAsync(Path.Combine(skill.FullName, "SKILL.md"), "host copy must not be read");
        const string workerRules = "Rules observed inside worker 😀";
        const string workerSkill = "---\nname: fixture\ndescription: Worker fixture skill\n---\nWorker-only skill body.\n";
        var broker = new FixtureBroker
        {
            Read = (path, _, _) => Task.FromResult(Encoding.UTF8.GetBytes(path == "AGENTS.md" ? workerRules : workerSkill))
        };
        var baseline = new BaselineTool();
        var extraFactoryCalls = 0;
        var engine = new AgentEngine(_store, [baseline], WorkspaceService.Resolve,
            additionalToolsFactory: (_, _, _) => { extraFactoryCalls++; throw new InvalidOperationException("Host integration factory must not run."); },
            executionBroker: broker);
        var model = new ScriptedModel(
            new("read", "worker_read", "{}"),
            new("host", "host_tool", "{}"),
            new("evidence", "inspect_evidence", "{}"),
            new("skill", "read_skill", "{\"name\":\"fixture\"}"));

        await engine.RunAsync(id, "Read the fixture skill and inspect evidence.", model,
            new(RunId: "worker-run", RequireOsIsolation: true, SecretEnvironmentVariables: ["CUSTOM_KEY"]), Emit, Approve);

        Assert.Equal(0, baseline.Calls);
        Assert.Equal(0, extraFactoryCalls);
        Assert.All(model.Requests, request =>
        {
            Assert.DoesNotContain(request.Tools, tool => tool.Name == "host_tool");
            Assert.Contains(request.Tools, tool => tool.Name == "worker_read");
            Assert.Contains(request.Tools, tool => tool.Name == "inspect_evidence");
            Assert.Contains(JsonSerializer.Serialize(workerRules, Wire.Json)[1..^1], request.Messages[0].Content);
            Assert.DoesNotContain("host copy", request.Messages[0].Content);
        });
        Assert.Equal("worker_read", Assert.Single(broker.Executions).Call.Name);
        Assert.Equal(0, broker.WorkspaceOnlyReads);
        Assert.NotEmpty(broker.Reads);
        Assert.All(broker.Reads, read => AssertOwner(read.Context, id, "worker-run"));
        Assert.All(broker.Reads, read => Assert.Equal(new[] { "CUSTOM_KEY" }, read.Context.ExcludedEnvironmentVariables));
        Assert.Contains(broker.Reads, read => read.Path == "AGENTS.md" && read.MaximumBytes == 32 * 1024);
        Assert.Contains(broker.Reads, read => read.Path.EndsWith("SKILL.md", StringComparison.Ordinal) && read.MaximumBytes == 64 * 1024);
        var saved = (await _store.LoadAsync(id))!;
        Assert.Contains(saved.Messages, message => message.ToolCallId == "host" && message.ToolIsError == true && message.Content.Contains("Unknown tool"));
        Assert.Contains(saved.Messages, message => message.ToolCallId == "skill" && message.ToolIsError == false && message.Content.Contains("Worker-only skill body"));
        Assert.Contains(saved.Messages, message => message.ToolCallId == "evidence" && message.ToolIsError == false);
        Assert.Equal("completed", saved.Info.Status);
        Assert.Equal(1, broker.Set.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IsolatedWritesStillRequireApprovalBeforeBrokerExecution(bool approved)
    {
        var id = await SessionAsync();
        var approvalObserved = false;
        var broker = new FixtureBroker
        {
            Execute = (_, _) =>
            {
                Assert.True(approvalObserved);
                return Task.FromResult(new ToolResult("observed worker change"));
            }
        };
        var engine = Engine(broker);
        await engine.RunAsync(id, "Make the proposed change.", new ScriptedModel(new ToolCall("write", "worker_write", "{}")),
            new(RequireOsIsolation: true, RunId: "approval-run"), Emit, async (request, _) =>
            {
                Assert.Equal("worker_write", request.ToolName);
                Assert.Equal("waiting_approval", (await _store.LoadAsync(id))!.Info.Status);
                Assert.Contains(await _store.EventsAsync(id), evt => evt.Type == "approval.requested" && evt.ApprovalId == request.Id);
                approvalObserved = true;
                return approved;
            });

        Assert.True(approvalObserved);
        Assert.Equal(approved ? 1 : 0, broker.Executions.Count);
        if (approved) AssertOwner(Assert.Single(broker.Executions).Context, id, "approval-run");
        var observation = Assert.Single((await _store.LoadAsync(id))!.Messages, message => message.Role == "tool");
        Assert.Equal(!approved, observation.ToolIsError);
        Assert.Contains(approved ? "observed worker change" : "declined", observation.Content);
    }

    [Fact]
    public async Task ReadOnlyModeWithIsolationCannotReachAMutatingWorkerTool()
    {
        var id = await SessionAsync();
        var broker = new FixtureBroker();
        var model = new ScriptedModel(new ToolCall("write", "worker_write", "{}"), new("read", "worker_read", "{}"));
        await Engine(broker).RunAsync(id, "Inspect only.", model,
            new(RequireOsIsolation: true, ExecutionMode: ExecutionMode.ReadOnly), Emit,
            (_, _) => throw new InvalidOperationException("Read-only cannot request write approval."));

        Assert.All(model.Requests, request => Assert.DoesNotContain(request.Tools, tool => tool.Name == "worker_write"));
        var executed = Assert.Single(broker.Executions);
        Assert.Equal("worker_read", executed.Call.Name);
        Assert.Equal(ExecutionMode.ReadOnly, executed.Context.ExecutionMode);
        Assert.Equal(ExecutionMode.ReadOnly, Assert.Single(broker.Created).ExecutionMode);
        Assert.Contains((await _store.LoadAsync(id))!.Messages, message => message.ToolCallId == "write" && message.ToolIsError == true);
    }

    [Theory]
    [InlineData(ExecutionMode.FullAccess, false)]
    [InlineData(ExecutionMode.WorkspaceWrite, true)]
    public async Task ApplicationAutoApprovalDoesNotBypassTheOsBoundary(ExecutionMode mode, bool autoApprove)
    {
        var id = await SessionAsync();
        var broker = new FixtureBroker();
        await Engine(broker).RunAsync(id, "Apply the authorized fixture change.",
            new ScriptedModel(new ToolCall("write", "worker_write", "{}")),
            new(RequireOsIsolation: true, ExecutionMode: mode, AutoApproveWrites: autoApprove), Emit,
            (_, _) => throw new InvalidOperationException("This application permission already authorizes the action."));

        var executed = Assert.Single(broker.Executions);
        Assert.Equal("worker_write", executed.Call.Name);
        Assert.True(executed.Context.RequireOsIsolation);
        Assert.Equal(mode, executed.Context.ExecutionMode);
    }

    [Theory]
    [InlineData("prefetch")]
    [InlineData("repository")]
    [InlineData("compression-file")]
    public async Task UnsupportedResearchReadersCannotEscapeTheRequiredBoundary(string reader)
    {
        var id = await SessionAsync();
        var research = reader switch
        {
            "prefetch" => new ResearchOptions(ReadOnlyPrefetch: true),
            "repository" => new ResearchOptions(RepositoryMemory: true),
            _ => new ResearchOptions(CompressionGuidance: true, CompressionGuidancePath: "profile.json")
        };
        var broker = new FixtureBroker();
        var model = new ScriptedModel();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Engine(broker).RunAsync(id, "Inspect safely.", model,
            new(RequireOsIsolation: true, Research: research), Emit, Approve));

        Assert.Empty(broker.Created);
        Assert.Empty(model.Requests);
        Assert.Empty(await _store.EventsAsync(id));
    }

    [Fact]
    public async Task RulesChangedDuringApprovalBlockThePreviouslyApprovedWorkerAction()
    {
        var id = await SessionAsync();
        await File.WriteAllTextAsync(Path.Combine(Workspace, "AGENTS.md"), "metadata fixture");
        var currentRules = "Original rules from worker.";
        var broker = new FixtureBroker { Read = (_, _, _) => Task.FromResult(Encoding.UTF8.GetBytes(currentRules)) };
        var model = new ScriptedModel(new ToolCall("write", "worker_write", "{}"));
        await Engine(broker).RunAsync(id, "Make a change after checking rules.", model, new(RequireOsIsolation: true), Emit,
            (_, _) => { currentRules = "New worker rules: review the additional constraint."; return Task.FromResult(true); });

        Assert.Empty(broker.Executions);
        Assert.Contains((await _store.LoadAsync(id))!.Messages,
            message => message.ToolCallId == "write" && message.ToolIsError == true && message.Content.Contains("new or changed"));
        Assert.Contains("New worker rules", model.Requests[^1].Messages[0].Content);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidWorkerRuleBytesFailClosedBeforeInference(bool oversized)
    {
        var id = await SessionAsync();
        await File.WriteAllTextAsync(Path.Combine(Workspace, "AGENTS.md"), "host content must not be the fallback");
        var broker = new FixtureBroker
        {
            Read = (_, _, _) => Task.FromResult(oversized ? new byte[32 * 1024 + 1] : new byte[] { 0xFF })
        };
        var model = new ScriptedModel();
        await Engine(broker).RunAsync(id, "Inspect rules safely.", model, new(RequireOsIsolation: true), Emit, Approve);

        Assert.Empty(model.Requests);
        Assert.Empty(broker.Executions);
        Assert.Equal(0, broker.WorkspaceOnlyReads);
        Assert.Equal(1, broker.Set.DisposeCalls);
        Assert.Equal("failed", (await _store.LoadAsync(id))!.Info.Status);
        Assert.Equal("run.failed", (await _store.EventsAsync(id))[^1].Type);
    }

    [Fact]
    public async Task ProjectMemoryHashesBoundedWorkerBytesInsteadOfTheHostCopy()
    {
        var id = await SessionAsync();
        const string workerContent = "content observed inside worker";
        await File.WriteAllTextAsync(Path.Combine(Workspace, "source.txt"), "different host copy");
        var source = await _store.AppendEventAsync(new(id, "source-run", "tool.completed",
            Call: new("source", "read_file", "{\"path\":\"source.txt\"}"),
            Result: new(JsonSerializer.Serialize(new
            {
                path = "source.txt", sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(workerContent))), content = workerContent
            }, Wire.Json))));
        var memory = new ProjectExperienceTools(_store, id, "source-run", Workspace, WorkspaceService.Resolve);
        var remembered = await memory.CreateTools().Single(tool => tool.Definition.Name == "remember_experience")
            .ExecuteAsync(new("remember", "remember_experience", JsonSerializer.Serialize(new
            {
                title = "Worker fixture procedure", preconditions = "Recheck source first", procedure = "Inspect source.txt before editing.", evidenceSequences = new[] { source.Sequence }
            }, Wire.Json)), new(Workspace));
        Assert.False(remembered.IsError, remembered.Content);
        var broker = new FixtureBroker { Read = (_, _, _) => Task.FromResult(Encoding.UTF8.GetBytes(workerContent)) };
        await Engine(broker).RunAsync(id, "Recall the worker procedure.",
            new ScriptedModel(new ToolCall("recall", "recall_project_memory", "{\"query\":\"Worker fixture\"}")),
            new(RequireOsIsolation: true, RunId: "memory-run"), Emit, Approve);

        Assert.Empty(broker.Executions);
        var read = Assert.Single(broker.Reads, read => read.Path == "source.txt");
        Assert.Equal(4 * 1024 * 1024, read.MaximumBytes);
        AssertOwner(read.Context, id, "memory-run");
        Assert.Equal(0, broker.WorkspaceOnlyReads);
        var recall = Assert.Single((await _store.LoadAsync(id))!.Messages, message => message.ToolCallId == "recall");
        using var result = JsonDocument.Parse(recall.Content);
        var match = Assert.Single(result.RootElement.GetProperty("matches").EnumerateArray());
        Assert.Equal("ReportedFilesMatchOnly", match.GetProperty("artifactStatus").GetString());
    }

    [Fact]
    public async Task CancellationWaitsForWorkerCleanupAndPersistsUnknownWithoutReplay()
    {
        var id = await SessionAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var broker = new FixtureBroker
        {
            Execute = async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new("unobserved result");
            }
        };
        broker.Set.OnDispose = async () =>
        {
            cleanupEntered.TrySetResult();
            await releaseCleanup.Task;
            order.Add("worker");
        };
        var resources = new FixtureResources { End = () => { order.Add("resources"); return Task.FromResult(0); } };
        var engine = Engine(broker, resources);
        using var cancellation = new CancellationTokenSource();
        var run = engine.RunAsync(id, "Inspect the fixture.", new ScriptedModel(new ToolCall("read", "worker_read", "{}")),
            new(RequireOsIsolation: true), Emit, Approve, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(run.IsCompleted);
            Assert.DoesNotContain(await _store.EventsAsync(id), evt => evt.Type is "run.completed" or "run.cancelled");
        }
        finally
        {
            cancellation.Cancel();
            releaseCleanup.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var stopped = (await _store.LoadAsync(id))!;
        Assert.Equal("cancelled", stopped.Info.Status);
        var observation = Assert.Single(stopped.Messages, message => message.Role == "tool");
        Assert.True(observation.ToolIsError);
        Assert.Contains("may or may not", observation.Content);
        Assert.Equal(new[] { "worker", "resources" }, order);
        Assert.Equal(1, broker.Set.DisposeCalls);
        var continuation = new ScriptedModel();
        await engine.ContinueAsync(id, continuation, new(RequireOsIsolation: true), Emit, Approve);
        Assert.Single(broker.Executions);
        Assert.Contains(continuation.Requests[0].Messages, message => message.Role == "tool" && message.Content.Contains("may or may not"));
        Assert.Equal("completed", (await _store.LoadAsync(id))!.Info.Status);
    }

    [Fact]
    public async Task WorkerCleanupFailureRemainsFailedAndStillCleansOtherOwnedResources()
    {
        var id = await SessionAsync();
        var broker = new FixtureBroker();
        broker.Set.OnDispose = () => throw new IOException("private cleanup diagnostic");
        var resources = new FixtureResources();
        await Engine(broker, resources).RunAsync(id, "Report only.", new ScriptedModel(), new(RequireOsIsolation: true), Emit, Approve);

        Assert.Equal(1, resources.Calls);
        Assert.Equal(1, broker.Set.DisposeCalls);
        Assert.Equal("failed", (await _store.LoadAsync(id))!.Info.Status);
        var events = await _store.EventsAsync(id);
        Assert.Contains(events, evt => evt.Type == "integrations.cleanup_failed" && evt.Text!.Contains("unknown"));
        Assert.DoesNotContain(events, evt => evt.Type == "run.completed" || evt.Text?.Contains("private cleanup diagnostic") == true);
        Assert.Equal("run.failed", events[^1].Type);
    }

    [Fact]
    public async Task InvalidWorkerCatalogCleansTheLeaseAndDurablyFailsWithoutHostFallback()
    {
        var id = await SessionAsync();
        var broker = new FixtureBroker(new WorkerTool("inspect_evidence"));
        var resources = new FixtureResources();
        var baseline = new BaselineTool();
        var model = new ScriptedModel();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AgentEngine(_store, [baseline], WorkspaceService.Resolve, resources, executionBroker: broker)
                .RunAsync(id, "Inspect safely.", model, new(RequireOsIsolation: true), Emit, Approve));

        Assert.Equal(1, broker.Set.DisposeCalls);
        Assert.Equal(1, resources.Calls);
        Assert.Empty(model.Requests);
        Assert.Equal(0, baseline.Calls);
        Assert.Empty(broker.Executions);
        var saved = (await _store.LoadAsync(id))!;
        Assert.Equal("failed", saved.Info.Status);
        Assert.Equal("failed", saved.RunCheckpoint!.StopReason);
        Assert.Equal("run.failed", (await _store.EventsAsync(id))[^1].Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringWorkerInitializationCleansResourcesBeforePublishingTheOutcome(bool cleanupFails)
    {
        var id = await SessionAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broker = new FixtureBroker
        {
            Create = async (_, token) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Cancelled worker creation cannot return a lease.");
            }
        };
        var resources = new FixtureResources
        {
            End = () => cleanupFails ? throw new IOException("private cleanup diagnostic") : Task.FromResult(0)
        };
        var model = new ScriptedModel();
        using var cancellation = new CancellationTokenSource();
        var run = Engine(broker, resources).RunAsync(id, "Initialize the worker.", model,
            new(RequireOsIsolation: true), Emit, Approve, cancellation.Token);
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { cancellation.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }

        Assert.Equal(1, resources.Calls);
        Assert.Empty(model.Requests);
        Assert.Empty(broker.Executions);
        Assert.Equal(0, broker.Set.DisposeCalls);
        var saved = (await _store.LoadAsync(id))!;
        Assert.Equal(cleanupFails ? "failed" : "cancelled", saved.Info.Status);
        Assert.Equal(cleanupFails ? "failed" : "cancelled", saved.RunCheckpoint!.StopReason);
        var events = await _store.EventsAsync(id);
        Assert.Equal(cleanupFails ? "run.failed" : "run.cancelled", events[^1].Type);
        Assert.DoesNotContain(events, evt => evt.Type == "run.completed" || evt.Text?.Contains("private cleanup diagnostic") == true);
        Assert.Equal(cleanupFails ? 1 : 0, events.Count(evt => evt.Type == "commands.cleanup_failed"));
    }

    private AgentEngine Engine(FixtureBroker broker, IRunResourceLifetime? resources = null) =>
        new(_store, [new BaselineTool()], WorkspaceService.Resolve, resources, executionBroker: broker);

    private async Task<string> SessionAsync()
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await _store.SaveAsync(new(new(id, "Isolation fixture", Workspace, now, now), []));
        return id;
    }

    private void AssertOwner(ToolExecutionContext context, string sessionId, string runId)
    {
        Assert.Equal(Workspace, context.WorkspacePath);
        Assert.Equal(sessionId, context.SessionId);
        Assert.Equal(runId, context.RunId);
        Assert.True(context.RequireOsIsolation);
    }

    private static ValueTask Emit(AgentEvent _, CancellationToken __) => ValueTask.CompletedTask;
    private static Task<bool> Approve(ApprovalRequest _, CancellationToken __) => Task.FromResult(true);
    public void Dispose() { _store.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_root, true); }

    private sealed class BaselineTool : IAgentTool
    {
        public int Calls;
        public ToolDefinition Definition => new("host_tool", "Host fixture", "{}");
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new ToolResult("unrestricted host execution")); }
    }

    private sealed class WorkerTool(string name, bool approval = false) : IAgentTool
    {
        public ToolDefinition Definition { get; } = new(name, "Worker fixture", "{\"type\":\"object\",\"properties\":{}}", approval);
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Worker tools must execute through the broker.");
    }

    private sealed class FixtureBroker(params IAgentTool[] tools) : IAgentToolExecutionBroker
    {
        public FixtureSet Set { get; } = new(tools.Length > 0 ? tools : [new WorkerTool("worker_read"), new WorkerTool("worker_write", true)]);
        public List<ToolExecutionContext> Created { get; } = [];
        public List<(ToolCall Call, ToolExecutionContext Context)> Executions { get; } = [];
        public List<(string Path, int MaximumBytes, ToolExecutionContext Context)> Reads { get; } = [];
        public int WorkspaceOnlyReads;
        public Func<ToolExecutionContext, CancellationToken, Task<IAgentToolSet?>>? Create { get; init; }
        public Func<ToolCall, CancellationToken, Task<ToolResult>>? Execute { get; init; }
        public Func<string, int, CancellationToken, Task<byte[]>>? Read { get; init; }
        public Task<IAgentToolSet?> CreateRunToolsAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
        { Created.Add(context); return Create?.Invoke(context, cancellationToken) ?? Task.FromResult<IAgentToolSet?>(Set); }
        public Task<ToolResult> ExecuteAsync(IAgentTool tool, ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Executions.Add((call, context));
            return Execute?.Invoke(call, cancellationToken) ?? Task.FromResult(new ToolResult("observed worker result"));
        }
        public Task<string> ReadWorkspaceTextAsync(string workspace, string path, int maximumCharacters, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A context-bound byte read is required.");
        public Task<byte[]> ReadWorkspaceBytesAsync(string workspace, string path, int maximumBytes, CancellationToken cancellationToken = default)
        { WorkspaceOnlyReads++; throw new InvalidOperationException("Workspace ownership is ambiguous without chat/run context."); }
        public Task<byte[]> ReadWorkspaceBytesAsync(ToolExecutionContext context, string path, int maximumBytes, CancellationToken cancellationToken = default)
        {
            Reads.Add((path, maximumBytes, context));
            return Read?.Invoke(path, maximumBytes, cancellationToken) ?? File.ReadAllBytesAsync(Path.Combine(context.WorkspacePath, path), cancellationToken);
        }
    }

    private sealed class FixtureSet(IReadOnlyList<IAgentTool> tools) : IAgentToolSet
    {
        public IReadOnlyList<IAgentTool> Tools => tools;
        public int DisposeCalls;
        public Func<Task>? OnDispose;
        public async ValueTask DisposeAsync() { DisposeCalls++; if (OnDispose is not null) await OnDispose(); }
    }

    private sealed class FixtureResources : IRunResourceLifetime
    {
        public int Calls;
        public Func<Task<int>>? End;
        public Task<int> EndRunAsync(string sessionId, string runId) { Calls++; return End?.Invoke() ?? Task.FromResult(0); }
    }

    private sealed class ScriptedModel(params ToolCall[] calls) : IAgentModel
    {
        public List<(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolDefinition> Tools)> Requests { get; } = [];
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var first = Requests.Count == 0;
            Requests.Add((messages.ToArray(), tools.ToArray()));
            await Task.Yield();
            yield return first && calls.Length > 0 ? new(ToolCalls: calls) : new(TextDelta: "Recorded fixture state.");
        }
    }
}
