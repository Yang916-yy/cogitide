using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Auth;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class DurableHostTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cogitide-durable-host-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Combine(_directory, "workspace");
    private string State => Path.Combine(_directory, "state");
    private string Database => Path.Combine(State, "sessions.db");

    public DurableHostTests()
    {
        Directory.CreateDirectory(Workspace);
        File.WriteAllText(Path.Combine(Workspace, "fixture.txt"), "observed fixture content");
        File.WriteAllText(Path.Combine(Workspace, "AGENTS.md"), "Preserve public interfaces and retain source evidence.");
    }

    [Fact]
    public async Task AcceptedReplyRequiresDurablePromptAndCheckpointBeforeBlockedInference()
    {
        var accounts = new Accounts { BlockInferenceCredential = true };
        var handler = new OpenAiResponsesTests.Handler((_, _) => throw new InvalidOperationException("The blocked provider must not receive a request."));
        using var http = new HttpClient(handler);
        await using var host = new WorkbenchHost(State, accounts, http);
        var id = (await Create(host)).Info.Id;
        var run = await Start(host, id, new(MaxIterations: 256, TimeoutSeconds: 60)).WaitAsync(TimeSpan.FromSeconds(5));
        await accounts.InferenceBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var store = new SqliteSessionStore(Database);
        var saved = (await store.LoadAsync(id))!;
        Assert.Equal("Original task: retain public interfaces.", Assert.Single(saved.Messages).Content);
        Assert.Equal(run, saved.RunCheckpoint!.RunId);
        Assert.Equal("running", saved.Info.Status);
        Assert.Contains(await store.EventsAsync(id), evt => evt.RunId == run && evt.Type == "run.accepted");
        await host.DispatchAsync("run.cancel", Wire.Element(new { sessionId = id }));
        Assert.Equal("run.cancelled", (await Terminal(host, id, run)).Type);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PausedHostContinuationRetainsOptionsInstructionsAndResultsWithoutReplayingActions()
    {
        var handler = new OpenAiResponsesTests.Handler((body, index) =>
        {
            var input = body.GetProperty("input");
            Assert.Contains("Original task: retain public interfaces.", input.GetRawText());
            var tools = body.GetProperty("tools")[0].GetProperty("tools");
            Assert.DoesNotContain(tools.EnumerateArray(), tool => tool.GetProperty("name").GetString() == "run_command");
            if (index == 1) return OpenAiResponsesTests.Sse(OpenAiResponsesTests.ToolResponse(index,
                toolName: "read_file", arguments: "{\"path\":\"fixture.txt\"}"));
            Assert.Contains("observed fixture content", input.GetRawText());
            return OpenAiResponsesTests.Sse(OpenAiResponsesTests.TextResponse("The existing observation is retained.", index));
        });
        using var http = new HttpClient(handler);
        await using var host = new WorkbenchHost(State, new Accounts(), http);
        var id = (await Create(host)).Info.Id;
        var original = new RunOptions(MaxIterations: 1, TimeoutSeconds: 60, MaxContextCharacters: 6000,
            AutoCompact: false, ExecutionMode: ExecutionMode.ReadOnly, MaxReportedTokens: 10000);
        var first = await Start(host, id, original);
        Assert.Equal("run.paused", (await Terminal(host, id, first)).Type);
        var paused = (AgentSession)await host.DispatchAsync("session.load", Wire.Element(new { sessionId = id }));
        Assert.Equal("iteration_limit", paused.RunCheckpoint!.StopReason);

        await Assert.ThrowsAsync<ArgumentException>(() => host.DispatchAsync("run.continue", Wire.Element(new
        {
            sessionId = id, provider = Provider(), images = new[] { new ImageAttachment("image/png", "iVBORw0KGgo=", "fixture.png") }
        })));
        var unchanged = (AgentSession)await host.DispatchAsync("session.load", Wire.Element(new { sessionId = id }));
        Assert.Equal(JsonSerializer.Serialize(paused, Wire.Json), JsonSerializer.Serialize(unchanged, Wire.Json));

        // A partial options object must inherit every omitted setting from the checkpoint.
        var receipt = Wire.Element(await host.DispatchAsync("run.continue", Wire.Element(new
        {
            sessionId = id, provider = Provider(), options = new { timeoutSeconds = 120 }
        })));
        var second = receipt.GetProperty("runId").GetString()!;
        Assert.Equal("run.completed", (await Terminal(host, id, second)).Type);
        var completed = (AgentSession)await host.DispatchAsync("session.load", Wire.Element(new { sessionId = id }));
        Assert.Equal(ExecutionMode.ReadOnly, completed.RunCheckpoint!.Options.ExecutionMode);
        Assert.False(completed.RunCheckpoint.Options.AutoCompact);
        Assert.Equal(6000, completed.RunCheckpoint.Options.MaxContextCharacters);
        Assert.Equal(10000, completed.RunCheckpoint.Options.MaxReportedTokens);
        Assert.Equal(1, completed.RunCheckpoint.Options.MaxIterations);
        Assert.Equal(120, completed.RunCheckpoint.Options.TimeoutSeconds);
        Assert.Equal(first, completed.RunCheckpoint.ParentRunId);
        Assert.NotEqual(first, second);
        Assert.Single(completed.Messages, message => message.Role == "user");
        Assert.Single(completed.Messages, message => message.Role == "tool");
        var events = await PageEvents(host, id);
        Assert.Single(events, evt => evt.Type == "tool.started" && evt.Call?.Name == "read_file");
        Assert.DoesNotContain(events, evt => evt.RunId == second && evt.Type == "tool.started");
        Assert.Equal(2, handler.Requests.Count);
        var completedJson = JsonSerializer.Serialize(completed, Wire.Json);
        var eventCount = events.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.DispatchAsync("run.continue", Wire.Element(new { sessionId = id, provider = Provider() })));
        Assert.Equal(completedJson, JsonSerializer.Serialize(await host.DispatchAsync("session.load", Wire.Element(new { sessionId = id })), Wire.Json));
        Assert.Equal(eventCount, (await PageEvents(host, id)).Count);
    }

    [Fact]
    public async Task ModelConstructionFailureDoesNotAcknowledgeOrPersistAnUnacceptedPromptAndReleasesLease()
    {
        var variable = "COGITIDE_HOST_FIXTURE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "fixture-key");
        try
        {
            var handler = new OpenAiResponsesTests.Handler((_, index) => OpenAiResponsesTests.Sse(OpenAiResponsesTests.TextResponse("Ready.", index)));
            using var http = new HttpClient(handler);
            await using var host = new WorkbenchHost(State, new Accounts(), http);
            var initial = await Create(host, "api");
            var id = initial.Info.Id;
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.DispatchAsync("run.start", Wire.Element(new
            {
                sessionId = id, prompt = "Unaccepted prompt", provider = Provider(variable) with { MaxOutputTokens = 0 }
            })).WaitAsync(TimeSpan.FromSeconds(5)));
            var failed = (AgentSession)await host.DispatchAsync("session.load", Wire.Element(new { sessionId = id }));
            Assert.Equal(JsonSerializer.Serialize(initial, Wire.Json), JsonSerializer.Serialize(failed, Wire.Json));
            Assert.Empty(await PageEvents(host, id));
            var retry = await Start(host, id, new(), variable);
            Assert.Equal("run.completed", (await Terminal(host, id, retry)).Type);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task HostHistoryRpcUsesBoundedSqlitePagesAndOriginalCursors()
    {
        await using var host = new WorkbenchHost(State, new Accounts());
        var id = (await Create(host)).Info.Id;
        using (var store = new SqliteSessionStore(Database))
        {
            var session = (await store.LoadAsync(id))!;
            var messages = Enumerable.Range(0, 1200).Select(index => new ChatMessage(index % 2 == 0 ? "user" : "assistant", "message " + index)).ToArray();
            await store.SaveAsync(session with { Messages = messages });
            for (var index = 0; index < 205; index++) await store.AppendEventAsync(new(id, "old", "fixture", index.ToString()));
        }
        var view = (SessionView)await host.DispatchAsync("session.view", Wire.Element(new { sessionId = id, limit = 70 }));
        Assert.True(view.Available);
        Assert.Equal(1200, view.TotalMessages);
        Assert.Equal(1130, view.FirstMessageIndex);
        Assert.Equal(70, view.Messages.Count);
        var previous = (SessionMessagePage)await host.DispatchAsync("session.messages.page", Wire.Element(new
        {
            sessionId = id, beforeIndex = view.FirstMessageIndex, limit = 70
        }));
        Assert.Equal(1060, previous.FirstMessageIndex);
        Assert.Equal("message 1129", previous.Messages[^1].Content);
        Assert.Equal(Enumerable.Range(1, 205).Select(index => (long)index), (await PageEvents(host, id)).Select(evt => evt.Sequence));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.DispatchAsync("session.events.page", Wire.Element(new { sessionId = id, limit = 1001 })));
    }

    private Task<AgentSession> Create(WorkbenchHost host, string backend = "chatgpt") => CreateCore(host, backend);
    private async Task<AgentSession> CreateCore(WorkbenchHost host, string backend) => (AgentSession)await host.DispatchAsync("session.create",
        Wire.Element(new { workspacePath = Workspace, backend }));
    private static ProviderSettings Provider(string variable = "IGNORED") => new(OpenAiResponsesModel.OfficialEndpoint,
        "fixture-responses", variable, Provider: ProviderKind.OpenAiResponses);
    private static async Task<string> Start(WorkbenchHost host, string id, RunOptions options, string variable = "IGNORED")
    {
        var receipt = Wire.Element(await host.DispatchAsync("run.start", Wire.Element(new
        {
            sessionId = id, prompt = "Original task: retain public interfaces.", provider = Provider(variable), options
        })));
        Assert.True(receipt.GetProperty("accepted").GetBoolean());
        return receipt.GetProperty("runId").GetString()!;
    }
    private static async Task<AgentEvent> Terminal(WorkbenchHost host, string id, string run)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var page = (SessionEventPage)await host.DispatchAsync("session.events.page", Wire.Element(new { sessionId = id, limit = 100 }));
            var terminal = page.Items.LastOrDefault(evt => evt.RunId == run && evt.Type is "run.completed" or "run.paused" or "run.cancelled" or "run.failed");
            if (terminal is not null) return terminal;
            await Task.Delay(20, deadline.Token);
        }
    }
    private static async Task<IReadOnlyList<AgentEvent>> PageEvents(WorkbenchHost host, string id)
    {
        List<AgentEvent> events = [];
        long? before = null;
        while (true)
        {
            var page = (SessionEventPage)await host.DispatchAsync("session.events.page", Wire.Element(new { sessionId = id, beforeSequence = before, limit = 61 }));
            Assert.True(page.Available);
            events.InsertRange(0, page.Items);
            if (!page.HasMore) return events;
            before = page.NextBeforeSequence;
        }
    }

    private sealed class Accounts : IChatGptAccountService
    {
        private int _credentialCalls;
        public bool BlockInferenceCredential { get; init; }
        public TaskCompletionSource InferenceBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<AccountState>? AccountChanged { add { } remove { } }
        public Task<AccountState> GetAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AccountState(true, true, AccountBinding: "durability-account"));
        public Task<LoginInfo> LoginAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Fixtures never open sign-in.");
        public Task CancelLoginAsync(string? loginId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<ChatGptCredential> GetCredentialAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _credentialCalls) > 1 && BlockInferenceCredential)
            {
                InferenceBlocked.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new("fixture-token", "durability-account", DateTimeOffset.UtcNow.AddHours(1));
        }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([new("fixture-responses", "Fixture Responses", SupportsImages: true)]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
