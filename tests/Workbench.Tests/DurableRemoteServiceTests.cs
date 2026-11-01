using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Auth;
using Workbench.Core;
using Workbench.Host.RemoteService;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class DurableRemoteServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-service-" + Guid.NewGuid().ToString("N"));

    public DurableRemoteServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task LoopbackServiceRequiresBearerPinnedIdentityAndNonBrowserClient()
    {
        var files = new RemoteServiceFiles(_root, create: true);
        await using var service = new RemoteHostService(files, (_, _) => Task.FromResult<object>(new { ok = true }));
        await service.StartAsync();
        var endpoint = service.Endpoint;
        Assert.Equal("127.0.0.1", new Uri(endpoint.Address).Host);
        Assert.Equal(64, endpoint.Token.Length);
        Assert.Equal(endpoint, await files.ReadAsync(endpoint.Identity.ServiceId));
        await Assert.ThrowsAsync<InvalidDataException>(() => files.ReadAsync(Guid.NewGuid().ToString("N")));
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new(endpoint.Address) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("identity")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        http.DefaultRequestHeaders.Add("X-Cogitide-Service", Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("identity")).StatusCode);
        http.DefaultRequestHeaders.Remove("X-Cogitide-Service");
        http.DefaultRequestHeaders.Add("X-Cogitide-Service", endpoint.Identity.ServiceId);
        http.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("identity")).StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("identity")).StatusCode);
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(files.EndpointPath);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
    }

    [Fact]
    public async Task AbortedHttpRequestDoesNotCancelOrReplayAcceptedDispatcherWork()
    {
        var began = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int executions = 0;
        await using var service = new RemoteHostService(new RemoteServiceFiles(_root, create: true), async (method, _) =>
        {
            if (method == "fixture.work")
            {
                Interlocked.Increment(ref executions); began.TrySetResult();
                await release.Task; finished.TrySetResult();
            }
            return new { executions };
        });
        await service.StartAsync();
        await using (var first = new RemoteServiceConnection(service.Endpoint))
        {
            await first.SubscribeAsync();
            using var abort = new CancellationTokenSource();
            var request = first.RequestAsync(new("one", "fixture.work", Wire.Element(new { })), abort.Token);
            await began.Task.WaitAsync(TimeSpan.FromSeconds(5));
            abort.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }
        Assert.False(finished.Task.IsCompleted);
        release.TrySetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var reconnected = new RemoteServiceConnection(service.Endpoint);
        await reconnected.SubscribeAsync();
        var reply = await reconnected.RequestAsync(new("two", "fixture.inspect", Wire.Element(new { })));
        Assert.Equal(1, reply.Result!.Value.GetProperty("executions").GetInt32());
    }

    [Fact]
    public async Task StdioBridgeEofDetachesWithoutStoppingServiceOrLosingNewNotifications()
    {
        await using var service = new RemoteHostService(new RemoteServiceFiles(_root, create: true), (_, _) => Task.FromResult<object>(new { ok = true }));
        await service.StartAsync();
        await RemoteServiceBridge.RunAsync(service.Endpoint, new StringReader(""), new StringWriter()).WaitAsync(TimeSpan.FromSeconds(5));
        await using var client = new RemoteServiceConnection(service.Endpoint);
        await client.SubscribeAsync();
        await service.PublishAsync(new("fixture.notification", Wire.Element(new { value = 17 })));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var notification = await client.ReadNotificationAsync(deadline.Token);
        Assert.Equal("fixture.notification", notification!.Method);
        Assert.Equal(17, notification.Params.GetProperty("value").GetInt32());
        Assert.Equal(service.Endpoint.Identity, await client.VerifyAsync(deadline.Token));
    }

    [Fact]
    public async Task ServiceShutdownStopsBridgeWhenInputReadIgnoresCancellationWithoutSendingCancelOrReplay()
    {
        var workAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var methods = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var input = new AdvisoryCancellationReader(JsonSerializer.Serialize(new RpcRequest("one", "fixture.work", Wire.Element(new { })), Wire.Json));
        Task? bridge = null;
        try
        {
            await using (var service = new RemoteHostService(new RemoteServiceFiles(_root, create: true), (method, _) =>
            {
                methods.Enqueue(method);
                if (method == "fixture.work") workAccepted.TrySetResult();
                return Task.FromResult<object>(new { accepted = true });
            }))
            {
                await service.StartAsync();
                bridge = RemoteServiceBridge.RunAsync(service.Endpoint, input, new StringWriter());
                await input.PendingRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await workAccepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(bridge.IsCompleted);
            }
            // The service's WebSocket closed, while the caller still holds stdin open. The bridge
            // must stop waiting locally rather than waiting for EOF or cancelling daemon work.
            await bridge.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(input.ReadCompleted);
            Assert.Equal(new[] { "fixture.work" }, methods.ToArray());
        }
        finally
        {
            input.Release();
            if (bridge is not null) await bridge.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class AdvisoryCancellationReader(string firstLine) : TextReader
    {
        private readonly TaskCompletionSource<string?> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _number;
        internal TaskCompletionSource PendingRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool ReadCompleted => _read.Task.IsCompleted;
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _number) == 1) return ValueTask.FromResult<string?>(firstLine);
            PendingRead.TrySetResult();
            return new(_read.Task); // Deliberately ignores cancellation, as some TextReaders do.
        }
        internal void Release() => _read.TrySetResult(null);
    }

    [Fact]
    public async Task ActualKernelSurvivesDisconnectAndReconnectExplicitCancelPersistsTerminalWithoutReplay()
    {
        var workspace = Path.Combine(_root, "workspace"); Directory.CreateDirectory(workspace);
        var state = Path.Combine(_root, "state"); Directory.CreateDirectory(state);
        var accounts = new FixtureAccounts(blockInference: true);
        using var http = new HttpClient(new OpenAiResponsesTests.Handler((_, _) => throw new InvalidOperationException("No provider request is expected.")));
        RemoteHostService? service = null;
        await using var host = new WorkbenchHost(state, accounts, http, notificationSink: n => service?.PublishAsync(n) ?? Task.CompletedTask);
        await host.InitializeAsync();
        await using (service = new RemoteHostService(new RemoteServiceFiles(state, create: true), host.DispatchAsync))
        {
            await service.StartAsync();
            string id;
            await using (var first = new RemoteServiceConnection(service.Endpoint))
            {
                await first.SubscribeAsync();
                var created = await Call<AgentSession>(first, "session.create", new { workspacePath = workspace, backend = "chatgpt" });
                id = created.Info.Id;
                var receipt = await Call<JsonElement>(first, "run.start", new { sessionId = id, prompt = "Wait without replaying actions.", provider = Provider(), options = new RunOptions(TimeoutSeconds: 60) });
                Assert.True(receipt.GetProperty("accepted").GetBoolean());
                await accounts.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.False(accounts.Cancelled.Task.IsCompleted);
            await using var second = new RemoteServiceConnection(service.Endpoint);
            await second.SubscribeAsync();
            var view = await Call<SessionView>(second, "session.view", new { sessionId = id });
            Assert.Equal("running", view.Info.Status);
            Assert.Single(view.Messages, m => m.Role == "user");
            await Call<JsonElement>(second, "run.cancel", new { sessionId = id });
            await accounts.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var events = await UntilEvents(second, id, items => items.Any(e => e.Type == "run.cancelled"));
            Assert.Single(events, e => e.Type == "run.accepted");
            Assert.DoesNotContain(events, e => e.Type == "tool.started");
            Assert.Equal(events.Select(e => e.Sequence).Distinct().Count(), events.Count);
            await host.ShutdownAsync();
        }
    }

    [Fact]
    public async Task PendingApprovalSurvivesOfflineAttachmentAndRequiresExplicitResponse()
    {
        var workspace = Path.Combine(_root, "workspace"); Directory.CreateDirectory(workspace);
        var state = Path.Combine(_root, "state"); Directory.CreateDirectory(state);
        using var http = new HttpClient(new OpenAiResponsesTests.Handler((_, index) => OpenAiResponsesTests.Sse(index == 1
            ? OpenAiResponsesTests.ToolResponse(index, toolName: "write_file", arguments: "{\"path\":\"approved.txt\",\"content\":\"must wait\",\"expectedSha256\":\"\"}")
            : OpenAiResponsesTests.TextResponse("The explicit approval decision was respected.", index))));
        RemoteHostService? service = null;
        await using var host = new WorkbenchHost(state, new FixtureAccounts(), http, notificationSink: n => service?.PublishAsync(n) ?? Task.CompletedTask);
        await host.InitializeAsync();
        await using (service = new RemoteHostService(new RemoteServiceFiles(state, create: true), host.DispatchAsync))
        {
            await service.StartAsync();
            string id;
            ApprovalRequest approval;
            await using (var first = new RemoteServiceConnection(service.Endpoint))
            {
                await first.SubscribeAsync();
                id = (await Call<AgentSession>(first, "session.create", new { workspacePath = workspace, backend = "chatgpt" })).Info.Id;
                await Call<JsonElement>(first, "run.start", new { sessionId = id, prompt = "Create a fixture file after approval.", provider = Provider(), options = new RunOptions(TimeoutSeconds: 60) });
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                RpcNotification? notification;
                do { notification = await first.ReadNotificationAsync(deadline.Token); } while (notification?.Method != "approval.requested");
                approval = notification.Params.Deserialize<ApprovalRequest>(Wire.Json)!;
            }
            Assert.False(File.Exists(Path.Combine(workspace, "approved.txt")));
            await using var second = new RemoteServiceConnection(service.Endpoint);
            await second.SubscribeAsync();
            var pending = await Call<ApprovalRequest[]>(second, "approval.pending", new { sessionId = id });
            Assert.Equal(approval, Assert.Single(pending));
            await Call<JsonElement>(second, "approval.respond", new { approvalId = approval.Id, approved = false });
            await UntilEvents(second, id, events => events.Any(e => e.Type == "run.completed"));
            Assert.False(File.Exists(Path.Combine(workspace, "approved.txt")));
            Assert.Empty(await Call<ApprovalRequest[]>(second, "approval.pending", new { sessionId = id }));
            await host.ShutdownAsync();
        }
    }

    private static ProviderSettings Provider() => new(OpenAiResponsesModel.OfficialEndpoint, "fixture-responses", "IGNORED", Provider: ProviderKind.OpenAiResponses);
    private static async Task<T> Call<T>(RemoteServiceConnection client, string method, object parameters)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reply = await client.RequestAsync(new(Guid.NewGuid().ToString("N"), method, Wire.Element(parameters)), deadline.Token);
        Assert.Null(reply.Error);
        return reply.Result!.Value.Deserialize<T>(Wire.Json)!;
    }
    private static async Task<IReadOnlyList<AgentEvent>> UntilEvents(RemoteServiceConnection client, string id, Func<IReadOnlyList<AgentEvent>, bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var page = await Call<SessionEventPage>(client, "session.events.page", new { sessionId = id, limit = 100 });
            if (ready(page.Items)) return page.Items;
            await Task.Delay(20, deadline.Token);
        }
    }

    private sealed class FixtureAccounts(bool blockInference = false) : IChatGptAccountService
    {
        private int _credentials;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<AccountState>? AccountChanged { add { } remove { } }
        public Task<AccountState> GetAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AccountState(true, true, AccountBinding: "service-fixture"));
        public Task<LoginInfo> LoginAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No sign-in in fixtures.");
        public Task CancelLoginAsync(string? loginId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<ChatGptCredential> GetCredentialAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _credentials) > 1 && blockInference)
            {
                Blocked.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            }
            return new("fixture-token", "service-fixture", DateTimeOffset.UtcNow.AddHours(1));
        }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([new("fixture-responses", "Fixture")]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
