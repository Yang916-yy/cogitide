using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class OpenAiResponsesTests
{
    private static readonly ProviderSettings Settings = new(OpenAiResponsesModel.OfficialEndpoint, "fixture-responses",
        "OPENAI_API_KEY", 4096, ProviderKind.OpenAiResponses, "high", SupportsImages: true);
    private static readonly ToolDefinition ReadTool = new("read", "Read generated fixture", "{\"type\":\"object\",\"properties\":{}}");

    [Fact]
    public async Task OAuthIgnoresApiOnlyOutputLimitAndApiImageCapabilityIsRespected()
    {
        var handler = new Handler((_, _) => Sse(TextResponse("Complete.")));
        using var http = new HttpClient(handler);
        var oauth = new OpenAiResponsesModel(Settings with { MaxOutputTokens = 393216 }, _ => Task.FromResult("fixture-oauth"), "account-A", http);
        await Collect(oauth.StreamAsync([new("user", "Hello")], []));
        Assert.False(handler.Requests[0].TryGetProperty("max_output_tokens", out _));
        var api = new OpenAiResponsesModel(Settings with { SupportsImages = false }, "fixture-key", http);
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(api.StreamAsync(
            [new("user", "Image", Images: [new("image/png", "iVBORw0KGgo=")])], [])));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task OAuthUsesPublicResponsesAndPreservesNativeItemsAcrossToolCallsAndSerialization()
    {
        var number = 0;
        var handler = new Handler((_, index) => Sse(index == 1 ? ToolResponse() : TextResponse("Finished.")));
        using var http = new HttpClient(handler);
        var model = new OpenAiResponsesModel(Settings, _ => Task.FromResult("oauth-token-" + ++number), "account-workspace-A", http);
        var image = new ImageAttachment("image/png", "iVBORw0KGgo=", "fixture.png");
        List<ChatMessage> history = [new("system", "Use the selected project."), new("user", "Inspect the fixture.", Images: [image])];
        var first = await Collect(model.StreamAsync(history, [ReadTool]));
        var complete = first[^1];
        var call = Assert.Single(complete.ToolCalls!);
        Assert.Equal("call_1", call.Id);
        Assert.Equal("read", call.Name);
        Assert.Equal(100, complete.InputTokens);
        Assert.Equal(25, complete.OutputTokens);
        Assert.Equal(12, complete.CachedInputTokens);
        Assert.Equal(4, complete.ReasoningTokens);
        Assert.DoesNotContain(first, update => (update.TextDelta ?? "").Contains("ciphertext", StringComparison.Ordinal));
        Assert.Equal("reasoning-ciphertext", complete.Continuation!.Payload.GetProperty("outputItems")[0].GetProperty("encrypted_content").GetString());
        Assert.Equal("cogito", complete.Continuation.Payload.GetProperty("outputItems")[1].GetProperty("namespace").GetString());
        history.Add(new("assistant", "", complete.ToolCalls, Continuation: complete.Continuation));
        history.Add(new("tool", "", ToolCallId: call.Id, ToolIsError: false));
        // Storage roundtrip uses the application's actual wire/source-generation settings.
        history = JsonSerializer.Deserialize<List<ChatMessage>>(JsonSerializer.Serialize(history, Wire.Json), Wire.Json)!;
        var second = (await Collect(model.StreamAsync(history, [ReadTool])))[^1];
        Assert.Equal("final_answer", second.Continuation!.Payload.GetProperty("outputItems")[0].GetProperty("phase").GetString());
        history.Add(new("assistant", "Finished.", Continuation: second.Continuation));
        history.Add(new("user", "Confirm."));
        await Collect(model.StreamAsync(history, [ReadTool]));
        Assert.Equal(3, number);
        Assert.Equal(new[] { "oauth-token-1", "oauth-token-2", "oauth-token-3" }, handler.Credentials);
        Assert.All(handler.Uris, uri => Assert.Equal("https://api.openai.com/v1/responses", uri));
        foreach (var body in handler.Requests)
        {
            Assert.False(body.GetProperty("store").GetBoolean());
            Assert.True(body.GetProperty("stream").GetBoolean());
            foreach (var field in new[] { "max_output_tokens", "previous_response_id", "conversation", "background", "metadata", "temperature", "top_p", "truncation" })
                Assert.False(body.TryGetProperty(field, out _), field);
            Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
            Assert.Contains(body.GetProperty("include").EnumerateArray(), value => value.GetString() == "reasoning.encrypted_content");
            var tool = Assert.Single(body.GetProperty("tools").EnumerateArray());
            Assert.Equal("namespace", tool.GetProperty("type").GetString());
            Assert.Equal("cogito", tool.GetProperty("name").GetString());
            Assert.Equal("read", tool.GetProperty("tools")[0].GetProperty("name").GetString());
            Assert.Equal("developer", body.GetProperty("input")[0].GetProperty("role").GetString());
        }
        var inputs = handler.Requests[1].GetProperty("input");
        Assert.Equal("data:image/png;base64," + image.Base64Data, inputs[1].GetProperty("content")[1].GetProperty("image_url").GetString());
        Assert.True(JsonElement.DeepEquals(complete.Continuation.Payload.GetProperty("outputItems")[0], inputs[2]));
        Assert.True(JsonElement.DeepEquals(complete.Continuation.Payload.GetProperty("outputItems")[1], inputs[3]));
        Assert.Equal("function_call_output", inputs[4].GetProperty("type").GetString());
        Assert.Equal("", inputs[4].GetProperty("output").GetString());
        Assert.True(JsonElement.DeepEquals(second.Continuation.Payload.GetProperty("outputItems")[0], handler.Requests[2].GetProperty("input")[5]));
    }

    [Fact]
    public async Task ApiKeyUsesSameEngineContractWithStandardFunctionToolsAndOutputLimit()
    {
        var handler = new Handler((_, _) => Sse(ToolResponse(toolNamespace: null)));
        using var http = new HttpClient(handler);
        var result = await Collect(new OpenAiResponsesModel(Settings, "api-key-fixture", http).StreamAsync([new("user", "Read")], [ReadTool]));
        Assert.Single(result[^1].ToolCalls!);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(4096, request.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("function", request.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, result[^1].Continuation!.Payload.GetProperty("credentialScope").ValueKind);
    }

    [Theory]
    [InlineData("missing-terminal")]
    [InlineData("unfinished-call")]
    [InlineData("incomplete")]
    [InlineData("wrong-namespace")]
    [InlineData("after-terminal")]
    [InlineData("changed-identity")]
    public async Task InvalidStreamsNeverExposeCallsOrContinuation(string failure)
    {
        var body = failure switch
        {
            "missing-terminal" => ToolResponse(terminal: false),
            "unfinished-call" => ToolResponse(done: false),
            "incomplete" => ToolResponse(terminalKind: "response.incomplete"),
            "wrong-namespace" => ToolResponse(toolNamespace: "someone_else"),
            "after-terminal" => ToolResponse() + Event("response.output_text.delta", new { output_index = 0, item_id = "changed", content_index = 0, delta = "extra" }),
            _ => ToolResponse(changedId: true)
        };
        using var http = new HttpClient(new Handler((_, _) => Sse(body)));
        List<ModelUpdate> received = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var update in OAuth(http).StreamAsync([new("user", "Read")], [ReadTool])) received.Add(update); });
        Assert.All(received, update => { Assert.Null(update.ToolCalls); Assert.Null(update.Continuation); });
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task HttpErrorsAreSanitizedAndNeverAutomaticallyRetried(int status)
    {
        var handler = new Handler((_, _) => new((HttpStatusCode)status) { Content = new StringContent("{\"error\":{\"message\":\"private-secret\"}}", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelRequestException>(() => Collect(OAuth(http).StreamAsync([new("user", "Read")], [])));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("private-secret", error.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task LatePlanLimitFailureIsSanitizedAndDoesNotCommitEarlierToolData()
    {
        var body = ToolResponse(terminal: false) + Event("response.failed", new { response = new { id = "resp_1", status = "failed", output = Array.Empty<object>(),
            error = new { code = "subscription_sharing_usage_limit_exceeded", message = "private-server-message" } } });
        using var http = new HttpClient(new Handler((_, _) => Sse(body)));
        var error = await Assert.ThrowsAsync<ModelRequestException>(() => Collect(OAuth(http).StreamAsync([new("user", "Read")], [ReadTool])));
        Assert.Contains("usage limit", error.Message);
        Assert.DoesNotContain("private-server-message", error.ToString());
    }

    [Fact]
    public async Task AccountAndEndpointBoundariesAreCheckedBeforeSendingCredentials()
    {
        var handler = new Handler((_, _) => Sse(TextResponse("Saved.")));
        using var http = new HttpClient(handler);
        var original = (await Collect(OAuth(http).StreamAsync([new("user", "Remember")], [])))[^1].Continuation!;
        var callbackCount = 0;
        var model = new OpenAiResponsesModel(Settings, _ => { callbackCount++; return Task.FromResult("must-not-be-requested"); }, "account-A", http);
        foreach (var state in new[] { original with { Model = "other-model" }, original with { Endpoint = "https://different.example/v1" },
            original with { Payload = Wire.Element(new { credentialScope = "account-B", outputItems = original.Payload.GetProperty("outputItems") }) } })
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => Collect(model.StreamAsync([new("assistant", "Saved.", Continuation: state), new("user", "Next")], [])));
        }
        Assert.Equal(0, callbackCount);
        Assert.Single(handler.Requests);
        Assert.Throws<ArgumentException>(() => new OpenAiResponsesModel(Settings with { Endpoint = "https://different.example/v1" }, _ => Task.FromResult("secret"), "account-A", http));
    }

    [Fact]
    public async Task OutOfOrderTextCannotCommitAReorderedAssistantMessage()
    {
        var body = Event("response.output_item.added", new { output_index = 0, item = new { type = "message", id = "msg_a", role = "assistant", status = "in_progress", content = Array.Empty<object>() } }) +
            Event("response.output_item.added", new { output_index = 1, item = new { type = "message", id = "msg_b", role = "assistant", status = "in_progress", content = Array.Empty<object>() } }) +
            Event("response.output_text.delta", new { output_index = 1, item_id = "msg_b", content_index = 0, delta = "second" }) +
            Event("response.output_text.delta", new { output_index = 0, item_id = "msg_a", content_index = 0, delta = "first" });
        using var http = new HttpClient(new Handler((_, _) => Sse(body)));
        List<ModelUpdate> received = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { await foreach (var update in OAuth(http).StreamAsync([new("user", "Read")], [])) received.Add(update); });
        Assert.Equal("second", Assert.Single(received).TextDelta);
        Assert.All(received, update => { Assert.Null(update.ToolCalls); Assert.Null(update.Continuation); });
    }

    [Fact]
    public async Task CancellationAfterCompleteToolItemStillDoesNotCommitWithoutTerminalResponse()
    {
        using var tail = new StalledStream(ToolResponse(terminal: false));
        using var http = new HttpClient(new Handler((_, _) => new(HttpStatusCode.OK) { Content = new StreamContent(tail) }));
        using var stop = new CancellationTokenSource();
        List<ModelUpdate> received = [];
        var work = Task.Run(async () => { await foreach (var update in OAuth(http).StreamAsync([new("user", "Read")], [ReadTool], stop.Token)) received.Add(update); });
        await tail.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.All(received, update => { Assert.Null(update.ToolCalls); Assert.Null(update.Continuation); });
    }

    [Fact]
    public async Task RealResponsesSdkUsesOwnToolsAndSummaryThenColdResumesWithoutReplayingActions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cogito-responses-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var normal = 0;
            var summaries = 0;
            var handler = new Handler((body, index) =>
            {
                var summary = !body.TryGetProperty("tools", out var tools) || tools.GetArrayLength() == 0;
                if (summary)
                {
                    summaries++;
                    Assert.DoesNotContain("reasoning-ciphertext", body.GetProperty("input").GetRawText());
                    return Sse(TextResponse("## Goal\nPreserve original constraint.\n## Progress\nObserved generated files.\n## Next Steps\nContinue inspection and verify current state.", index));
                }
                normal++;
                Assert.Contains("Preserve original constraint", body.GetProperty("input").GetRawText());
                return Sse(normal <= 7 ? ToolResponse(number: index) : TextResponse("Done.", index));
            });
            using var http = new HttpClient(handler);
            var tool = new FixtureTool();
            string id;
            CompactionCheckpoint checkpoint;
            using (var store = new SqliteSessionStore(Path.Combine(directory, "sessions.db")))
            {
                id = Guid.NewGuid().ToString("N");
                await store.SaveAsync(new(new(id, "Responses integration", directory, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Backend: "chatgpt", AccountBinding: "account-A"), []));
                await new AgentEngine(store, [tool]).RunAsync(id, "Preserve original constraint", OAuth(http), new(MaxIterations: 12, MaxContextCharacters: 10000),
                    (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
                var saved = (await store.LoadAsync(id))!;
                Assert.Equal("completed", saved.Info.Status);
                Assert.Equal(7, tool.Calls);
                Assert.True(summaries > 0);
                checkpoint = Assert.IsType<CompactionCheckpoint>(saved.Compaction);
                Assert.Equal(7, saved.Messages.Count(message => message.Role == "tool"));
                Assert.Contains(saved.Messages, message => message.Continuation is not null);
                Assert.All(await store.EventsAsync(id), evt => Assert.DoesNotContain("reasoning-ciphertext", evt.Text ?? ""));
            }
            using var reopened = new SqliteSessionStore(Path.Combine(directory, "sessions.db"));
            Assert.Equal(checkpoint, (await reopened.LoadAsync(id))!.Compaction);
            await new AgentEngine(reopened, [tool]).RunAsync(id, "Only report current progress.", OAuth(http), new(MaxContextCharacters: 10000),
                (_, _) => ValueTask.CompletedTask, (_, _) => Task.FromResult(true));
            Assert.Equal(7, tool.Calls);
            Assert.Equal("completed", (await reopened.LoadAsync(id))!.Info.Status);
        }
        finally { SqliteConnection.ClearAllPools(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    private static OpenAiResponsesModel OAuth(HttpClient http) => new(Settings, _ => Task.FromResult("fixture-oauth"), "account-A", http);
    private static async Task<List<ModelUpdate>> Collect(IAsyncEnumerable<ModelUpdate> stream)
    { List<ModelUpdate> result = []; await foreach (var item in stream) result.Add(item); return result; }
    internal static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    internal static string Event(string type, object values)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(values))!;
        fields["type"] = JsonSerializer.SerializeToElement(type);
        fields["sequence_number"] = JsonSerializer.SerializeToElement(1);
        return "event: " + type + "\ndata: " + JsonSerializer.Serialize(fields) + "\n\n";
    }
    private static object Usage => new { input_tokens = 100, output_tokens = 25, total_tokens = 125,
        input_tokens_details = new { cached_tokens = 12 }, output_tokens_details = new { reasoning_tokens = 4 } };
    internal static string ToolResponse(int number = 1, string? toolNamespace = "cogito", bool terminal = true, bool done = true,
        string terminalKind = "response.completed", bool changedId = false, string toolName = "read", string arguments = "{}")
    {
        var reason = new { type = "reasoning", id = "rs_" + number, summary = Array.Empty<object>(), status = "completed", encrypted_content = "reasoning-ciphertext" };
        var fields = new Dictionary<string, object?> { ["type"] = "function_call", ["id"] = "fc_" + number, ["call_id"] = "call_" + number,
            ["name"] = toolName, ["arguments"] = arguments, ["status"] = "completed" };
        if (toolNamespace is not null) fields["namespace"] = toolNamespace;
        var added = new Dictionary<string, object?>(fields) { ["arguments"] = "", ["status"] = "in_progress" };
        var body = Event("response.output_item.added", new { output_index = 0, item = new { type = "reasoning", id = reason.id, summary = Array.Empty<object>(), status = "in_progress" } }) +
            Event("response.output_item.done", new { output_index = 0, item = new { type = "reasoning", id = reason.id, summary = Array.Empty<object>(), status = "completed" } }) +
            Event("response.output_item.added", new { output_index = 1, item = added }) +
            Event("response.function_call_arguments.delta", new { output_index = 1, item_id = "fc_" + number, delta = arguments });
        if (done) body += Event("response.output_item.done", new { output_index = 1, item = changedId ? new Dictionary<string, object?>(fields) { ["id"] = "fc_other" } : fields });
        if (terminal) body += Event(terminalKind, new { response = new { id = "resp_" + number, status = terminalKind == "response.completed" ? "completed" : "incomplete",
            output = new object[] { reason, fields }, usage = Usage } });
        return body;
    }
    internal static string TextResponse(string text, int number = 1)
    {
        var item = new { type = "message", id = "msg_" + number, role = "assistant", status = "completed", phase = "final_answer",
            content = new[] { new { type = "output_text", text, annotations = Array.Empty<object>() } } };
        return Event("response.output_item.added", new { output_index = 0, item = new { type = "message", id = item.id, role = "assistant", status = "in_progress", content = Array.Empty<object>() } }) +
            Event("response.output_text.delta", new { output_index = 0, content_index = 0, item_id = item.id, delta = text }) +
            Event("response.output_item.done", new { output_index = 0, item }) +
            Event("response.completed", new { response = new { id = "resp_" + number, status = "completed", output = new[] { item }, usage = Usage } });
    }
    internal sealed class Handler(Func<JsonElement, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];
        public List<string?> Credentials { get; } = [];
        public List<string?> Uris { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Deserialize<JsonElement>(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(json); Credentials.Add(request.Headers.Authorization?.Parameter); Uris.Add(request.RequestUri?.AbsoluteUri);
            return respond(json, Requests.Count);
        }
    }
    private sealed class FixtureTool : IAgentTool
    {
        public ToolDefinition Definition => ReadTool;
        public int Calls { get; private set; }
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new ToolResult(new string('x', 3000))); }
    }
    private sealed class StalledStream(string prefix) : Stream
    {
        private readonly MemoryStream _prefix = new(Encoding.UTF8.GetBytes(prefix));
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _prefix.ReadAsync(buffer, cancellationToken);
            if (read > 0) return read;
            Waiting.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _prefix.Dispose(); base.Dispose(disposing); }
    }
}
