using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class ModelEngineIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cogito-model-integration-" + Guid.NewGuid().ToString("N"));
    private static readonly ProviderSettings Settings = new("https://api.anthropic.com", "claude-sonnet-4-6",
        "ANTHROPIC_API_KEY", 4096, ProviderKind.Anthropic, "high");
    private string Database => Path.Combine(_directory, "sessions.db");
    public ModelEngineIntegrationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task RealSdkToolTurnPersistsOpaqueStateImagesAndErrorsAcrossRestart()
    {
        var bodies = new Queue<string>([AnthropicModelTests.ReasoningToolResponse(), AnthropicModelTests.TextResponse("Done."), AnthropicModelTests.TextResponse("Continued.")]);
        var handler = new RequestHandler(() => Response(bodies.Dequeue()));
        using var http = new HttpClient(handler);
        var tool = new SearchTool();
        var image = new ImageAttachment("image/png", "iVBORw0KGgo=", "fixture.png");
        string id;
        JsonElement savedState;
        using (var store = new SqliteSessionStore(Database))
        {
            id = await Session(store);
            await new AgentEngine(store, [tool]).RunAsync(id, "Inspect this image and search", new AnthropicModel(Settings, "fixture-key", http),
                new(), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true), images: [image]);
            var session = (await store.LoadAsync(id))!;
            Assert.Equal("completed", session.Info.Status);
            Assert.Equal(1, tool.Calls);
            Assert.Equal(image, Assert.Single(Assert.Single(session.Messages, message => message.Role == "user").Images!));
            var assistant = Assert.Single(session.Messages, message => message.ToolCalls is { Count: > 0 });
            savedState = assistant.Continuation!.Payload.Clone();
            Assert.Equal("signed-token", savedState[0].GetProperty("signature").GetString());
            Assert.True(Assert.Single(session.Messages, message => message.Role == "tool").ToolIsError);
            var secondRequest = handler.Requests[1].GetProperty("messages");
            Assert.True(JsonElement.DeepEquals(savedState, secondRequest[1].GetProperty("content")));
            Assert.True(secondRequest[2].GetProperty("content")[0].GetProperty("is_error").GetBoolean());
            var events = await store.EventsAsync(id);
            Assert.All(events, evt => Assert.DoesNotContain("private reasoning", evt.Text ?? ""));
            Assert.All(events, evt => Assert.DoesNotContain("signed-token", evt.Text ?? ""));
            Assert.All(events.Where(evt => evt.Type == "model.usage"), evt =>
            {
                using var usage = JsonDocument.Parse(evt.Text!);
                Assert.Equal(20, usage.RootElement.GetProperty("cachedInputTokens").GetInt64());
                Assert.Equal(30, usage.RootElement.GetProperty("cacheWriteTokens").GetInt64());
                Assert.Equal(8, usage.RootElement.GetProperty("reasoningTokens").GetInt64());
            });
        }

        using var restarted = new SqliteSessionStore(Database);
        var restored = (await restarted.LoadAsync(id))!;
        Assert.True(JsonElement.DeepEquals(savedState, Assert.Single(restored.Messages, message => message.ToolCalls is { Count: > 0 }).Continuation!.Payload));
        await new AgentEngine(restarted, [tool]).RunAsync(id, "Continue from those results", new AnthropicModel(Settings, "fixture-key", http),
            new(), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        Assert.Equal(1, tool.Calls);
        Assert.Equal(3, handler.Requests.Count);
        var resumed = handler.Requests[2].GetProperty("messages");
        Assert.True(JsonElement.DeepEquals(savedState, resumed[1].GetProperty("content")));
        Assert.Equal(image.Base64Data, resumed[0].GetProperty("content")[0].GetProperty("source").GetProperty("data").GetString());
        Assert.Equal("completed", (await restarted.LoadAsync(id))!.Info.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeInterruptionAndCancellationCannotPersistHalfStateOrExecuteTools(bool cancel)
    {
        var complete = AnthropicModelTests.ReasoningToolResponse();
        var incomplete = complete[..complete.IndexOf("event: message_stop", StringComparison.Ordinal)];
        using var tail = new StalledAfterPrefixStream(incomplete);
        var handler = new RequestHandler(() => cancel
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(tail) }
            : Response(incomplete));
        using var http = new HttpClient(handler);
        using var store = new SqliteSessionStore(Database);
        var id = await Session(store);
        var tool = new SearchTool();
        using var stop = new CancellationTokenSource();
        var run = new AgentEngine(store, [tool]).RunAsync(id, "Inspect", new AnthropicModel(Settings, "fixture-key", http),
            new(), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true), stop.Token);
        if (cancel)
        {
            await tail.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();
        }
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        var session = (await store.LoadAsync(id))!;
        Assert.Equal(cancel ? "cancelled" : "failed", session.Info.Status);
        Assert.Equal(0, tool.Calls);
        Assert.All(session.Messages, message => { Assert.Null(message.Continuation); Assert.Null(message.ToolCalls); });
        Assert.DoesNotContain(await store.EventsAsync(id), evt => evt.Type == "tool.started" || evt.Type == "model.usage");
    }

    [Fact]
    public async Task EngineDiscardsStateEvenIfAMisbehavingAdapterYieldsItBeforeFailure()
    {
        using var store = new SqliteSessionStore(Database);
        var id = await Session(store);
        var tool = new SearchTool();
        await new AgentEngine(store, [tool]).RunAsync(id, "Inspect", new PrematureStateModel(), new(),
            (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
        var saved = (await store.LoadAsync(id))!;
        Assert.Equal("failed", saved.Info.Status);
        Assert.All(saved.Messages, message => Assert.Null(message.Continuation));
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task InvalidImageIsRejectedBeforeRunStateOrNetworkChanges()
    {
        using var store = new SqliteSessionStore(Database);
        var id = await Session(store);
        var handler = new RequestHandler(() => Response(AnthropicModelTests.TextResponse("Unused")));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentException>(() => new AgentEngine(store, []).RunAsync(id, "Inspect", new AnthropicModel(Settings, "fixture-key", http),
            new(), (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true), images: [new("image/png", Convert.ToBase64String("not an image"u8))]));
        Assert.Empty((await store.LoadAsync(id))!.Messages);
        Assert.Empty(await store.EventsAsync(id));
        Assert.Empty(handler.Requests);
    }

    private async Task<string> Session(SqliteSessionStore store)
    {
        var id = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new(new(id, "Native fixture", _directory, now, now), []));
        return id;
    }

    private static HttpResponseMessage Response(string sse) => new(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };

    private sealed class RequestHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(body.RootElement.Clone());
            return response();
        }
    }

    private sealed class SearchTool : IAgentTool
    {
        public int Calls { get; private set; }
        public ToolDefinition Definition { get; } = new("search", "Search files", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}}}");
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new ToolResult("Fixture search failed", IsError: true)); }
    }

    private sealed class PrematureStateModel : IAgentModel
    {
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var content = JsonDocument.Parse("[{\"type\":\"thinking\",\"thinking\":\"private fixture\",\"signature\":\"fixture\"}]");
            yield return new(ToolCalls: [new("call_1", "search", "{}")], Continuation: new(AnthropicModel.ContinuationFormat, Settings.Endpoint, Settings.Model, content.RootElement.Clone()));
            await Task.Yield();
            throw new IOException("Fixture stream ended unexpectedly");
        }
    }

    private sealed class StalledAfterPrefixStream(string prefix) : Stream
    {
        private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(prefix));
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _prefix.ReadAsync(buffer, cancellationToken);
            if (read > 0) return read;
            Waiting.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        protected override void Dispose(bool disposing) { if (disposing) _prefix.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }
}
