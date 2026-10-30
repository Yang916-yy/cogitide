using System.Net;
using System.Text;
using System.Text.Json;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class AnthropicModelTests
{
    private static readonly ProviderSettings Settings = new("https://api.anthropic.com", "claude-sonnet-4-6",
        "ANTHROPIC_API_KEY", 4096, ProviderKind.Anthropic, "high");
    private static readonly ToolDefinition Search = new("search", "Search files", "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"}},\"required\":[\"query\"]}");

    [Fact]
    public async Task NativeRequestAndSignedThinkingRoundTripPreserveOrderedBlocks()
    {
        var handler = new FixtureHandler(() => Sse(ReasoningToolResponse()));
        using var http = new HttpClient(handler);
        var model = new AnthropicModel(Settings, "fixture-key", http);
        var png = new ImageAttachment("image/png", "iVBORw0KGgo=");
        var updates = await Collect(model.StreamAsync([new("system", "Project rules"), new("user", "Find it", Images: [png])], [Search]));
        Assert.Equal("Searching.", string.Concat(updates.Select(update => update.TextDelta)));
        Assert.All(updates[..^1], update => Assert.Null(update.ToolCalls));
        var final = updates[^1];
        var call = Assert.Single(final.ToolCalls!);
        Assert.Equal("{\"query\":\"fixture\"}", call.ArgumentsJson);
        Assert.Equal(60, final.InputTokens);
        Assert.Equal(12, final.OutputTokens);
        Assert.Equal(20, final.CachedInputTokens);
        Assert.Equal(30, final.CacheWriteTokens);
        Assert.Equal(8, final.ReasoningTokens);
        var state = Assert.IsType<ModelContinuationState>(final.Continuation);
        var blocks = state.Payload.EnumerateArray().ToArray();
        Assert.Equal(["thinking", "redacted_thinking", "text", "tool_use"], blocks.Select(block => block.GetProperty("type").GetString()));
        Assert.Equal("private reasoning", blocks[0].GetProperty("thinking").GetString());
        Assert.Equal("signed-token", blocks[0].GetProperty("signature").GetString());
        Assert.Equal("opaque", blocks[0].GetProperty("provider_extension").GetString());
        Assert.Equal("encrypted-thought", blocks[1].GetProperty("data").GetString());
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.LastUri!.AbsoluteUri);
        Assert.Equal("fixture-key", handler.ApiKey);
        Assert.NotNull(handler.Version);
        var first = handler.Bodies[0];
        Assert.Equal("adaptive", first.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", first.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("Project rules", first.GetProperty("system").GetString());
        Assert.Equal("image", first.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("base64", first.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("search", first.GetProperty("tools")[0].GetProperty("name").GetString());

        // Mimic a checkpoint JSON round-trip rather than passing the SDK's in-memory objects.
        var restored = JsonSerializer.Deserialize<ModelContinuationState>(JsonSerializer.Serialize(state))!;
        await Collect(model.StreamAsync([new("user", "Find it"),
            new("assistant", "Searching.", final.ToolCalls, Continuation: restored),
            new("tool", "No match", ToolCallId: call.Id, ToolIsError: true)], [Search]));
        var second = handler.Bodies[1].GetProperty("messages");
        Assert.True(JsonElement.DeepEquals(state.Payload, second[1].GetProperty("content")));
        var result = second[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.True(result.GetProperty("is_error").GetBoolean());
        Assert.Equal(call.Id, result.GetProperty("tool_use_id").GetString());
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("missing_block_stop")]
    [InlineData("wrong_stop")]
    [InlineData("trailing")]
    [InlineData("wrong_delta")]
    [InlineData("duplicate_id")]
    [InlineData("unknown_tool")]
    [InlineData("malformed_json")]
    [InlineData("malformed_tool_json")]
    [InlineData("array_tool_json")]
    [InlineData("missing_signature")]
    public async Task InvalidStreamsNeverReleaseToolsOrContinuation(string fault)
    {
        var response = fault switch
        {
            "truncated" => ToolPrefix(),
            "missing_block_stop" => ToolPrefix() + End("tool_use"),
            "wrong_stop" => ToolPrefix() + BlockStop(0) + End("end_turn"),
            "trailing" => ToolPrefix() + BlockStop(0) + End("tool_use") + BlockStart(1, new { type = "text", text = "late" }),
            "wrong_delta" => Start() + BlockStart(0, new { type = "text", text = "" }) + Delta(0, new { type = "input_json_delta", partial_json = "{}" }) + BlockStop(0) + End("end_turn"),
            "duplicate_id" => ToolPrefix() + BlockStop(0) + BlockStart(1, new { type = "tool_use", id = "call_1", name = "search", input = new { } }) + BlockStop(1) + End("tool_use"),
            "unknown_tool" => Start() + BlockStart(0, new { type = "tool_use", id = "call_1", name = "not_registered", input = new { } }) + BlockStop(0) + End("tool_use"),
            "malformed_tool_json" => ToolPrefix() + Delta(0, new { type = "input_json_delta", partial_json = "{\"query\":" }) + BlockStop(0) + End("tool_use"),
            "array_tool_json" => Start() + BlockStart(0, new { type = "tool_use", id = "call_1", name = "search", input = new { } }) + Delta(0, new { type = "input_json_delta", partial_json = "[]" }) + BlockStop(0) + End("tool_use"),
            "missing_signature" => Start() + BlockStart(0, new { type = "thinking", thinking = "opaque", signature = "" }) + BlockStop(0) + End("end_turn"),
            _ => Start() + "event: content_block_start\ndata: {broken-json\n\n"
        };
        using var http = new HttpClient(new FixtureHandler(() => Sse(response)));
        List<ModelUpdate> received = [];
        var error = await Assert.ThrowsAsync<ModelRequestException>(async () =>
        {
            await foreach (var update in new AnthropicModel(Settings, "fixture-key", http).StreamAsync([new("user", "test")], [Search])) received.Add(update);
        });
        Assert.All(received, update => { Assert.Null(update.ToolCalls); Assert.Null(update.Continuation); });
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(503)]
    public async Task HttpErrorsAreSanitizedAndNeverRetried(int status)
    {
        var handler = new FixtureHandler(() => new((HttpStatusCode)status)
        { Content = new StringContent("{\"error\":{\"message\":\"private-upstream-secret\"}}", Encoding.UTF8, "application/json") });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelRequestException>(() => Collect(new AnthropicModel(Settings, "fixture-key", http).StreamAsync([new("user", "test")], [])));
        Assert.Equal(status, error.StatusCode);
        Assert.DoesNotContain("private-upstream-secret", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelAndDeadlineCoverAStreamStalledAfterHeaders(bool timeout)
    {
        using var body = new StalledStream();
        var handler = new FixtureHandler(() => new(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var http = new HttpClient(handler);
        using var cancel = new CancellationTokenSource();
        var model = new AnthropicModel(Settings, "fixture-key", http, timeout ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(10));
        var task = Collect(model.StreamAsync([new("user", "test")], [], cancel.Token));
        await body.Reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (timeout)
        {
            var error = await Assert.ThrowsAsync<ModelRequestException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains("timed out", error.Message);
        }
        else
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ContinuationCannotCrossModelOrEndpointOrAlterToolCalls()
    {
        var handler = new FixtureHandler(() => Sse(ReasoningToolResponse()));
        using var http = new HttpClient(handler);
        var final = (await Collect(new AnthropicModel(Settings, "fixture-key", http).StreamAsync([new("user", "test")], [Search])))[^1];
        foreach (var settings in new[] { Settings with { Model = "claude-opus-4-6" }, Settings with { Endpoint = "https://different.example" } })
            await Assert.ThrowsAsync<InvalidDataException>(() => Collect(new AnthropicModel(settings, "fixture-key", http)
                .StreamAsync([new("assistant", "Searching.", final.ToolCalls, Continuation: final.Continuation)], [Search])));
        await Assert.ThrowsAsync<ModelRequestException>(() => Collect(new AnthropicModel(Settings, "fixture-key", http)
            .StreamAsync([new("assistant", "Tampered text", final.ToolCalls, Continuation: final.Continuation)], [Search])));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OrphanDuplicateAndMissingToolResultsFailBeforeSending()
    {
        var handler = new FixtureHandler(() => Sse(Start() + End("end_turn")));
        using var http = new HttpClient(handler);
        ChatMessage call = new("assistant", "", [new("call_1", "search", "{}")]);
        ChatMessage result = new("tool", "done", ToolCallId: "call_1");
        ChatMessage[][] broken = [
            [new("user", "test"), result],
            [new("user", "test"), call],
            [new("user", "test"), call, result, result],
            [new("user", "test"), call, new("tool", "done", ToolCallId: "wrong")],
            [new("user", "test"), call, new("user", "next"), result]
        ];
        foreach (var history in broken)
            await Assert.ThrowsAsync<InvalidDataException>(() => Collect(new AnthropicModel(Settings, "fixture-key", http).StreamAsync(history, [Search])));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task ManualThinkingIsExplicitAndRejectedOnModernModels()
    {
        Assert.Throws<ArgumentException>(() => new AnthropicModel(Settings with { Model = "claude-sonnet-4-7", ThinkingBudgetTokens = 2048 }, "fixture-key"));
        Assert.Throws<ArgumentException>(() => new AnthropicModel(Settings with { ThinkingBudgetTokens = 4096 }, "fixture-key"));
        var handler = new FixtureHandler(() => Sse(Start() + BlockStart(0, new { type = "text", text = "Done" }) + BlockStop(0) + End("end_turn")));
        using var http = new HttpClient(handler);
        await Collect(new AnthropicModel(Settings with { ThinkingBudgetTokens = 2048 }, "fixture-key", http).StreamAsync([new("user", "test")], []));
        Assert.Equal("enabled", handler.Bodies[0].GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(2048, handler.Bodies[0].GetProperty("thinking").GetProperty("budget_tokens").GetInt32());
    }

    internal static string ReasoningToolResponse() => Start() +
        BlockStart(0, new { type = "thinking", thinking = "", signature = "", provider_extension = "opaque" }) +
        Delta(0, new { type = "thinking_delta", thinking = "private reasoning" }) +
        Delta(0, new { type = "signature_delta", signature = "signed-" }) + Delta(0, new { type = "signature_delta", signature = "token" }) + BlockStop(0) +
        BlockStart(1, new { type = "redacted_thinking", data = "encrypted-thought" }) + BlockStop(1) +
        BlockStart(2, new { type = "text", text = "" }) + Delta(2, new { type = "text_delta", text = "Searching." }) + BlockStop(2) +
        BlockStart(3, new { type = "tool_use", id = "call_1", name = "search", input = new { } }) +
        Delta(3, new { type = "input_json_delta", partial_json = "{\"query\":" }) + Delta(3, new { type = "input_json_delta", partial_json = "\"fixture\"}" }) + BlockStop(3) + End("tool_use");
    private static string ToolPrefix() => Start() + BlockStart(0, new { type = "tool_use", id = "call_1", name = "search", input = new { } }) + Delta(0, new { type = "input_json_delta", partial_json = "{}" });
    internal static string TextResponse(string text) => Start() + BlockStart(0, new { type = "text", text }) + BlockStop(0) + End("end_turn");
    private static string Start() => Event("message_start", new { type = "message_start", message = new { id = "msg_fixture", type = "message", role = "assistant", model = "claude-sonnet-4-6", content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null, usage = new { input_tokens = 10, output_tokens = 0, cache_creation_input_tokens = 30, cache_read_input_tokens = 20 } } });
    private static string End(string reason) => Event("message_delta", new { type = "message_delta", delta = new { stop_reason = reason, stop_sequence = (string?)null }, usage = new { output_tokens = 12, output_tokens_details = new { thinking_tokens = 8 } } }) + Event("message_stop", new { type = "message_stop" });
    private static string BlockStart(int index, object block) => Event("content_block_start", new { type = "content_block_start", index, content_block = block });
    private static string BlockStop(int index) => Event("content_block_stop", new { type = "content_block_stop", index });
    private static string Delta(int index, object delta) => Event("content_block_delta", new { type = "content_block_delta", index, delta });
    private static string Event(string name, object value) => $"event: {name}\ndata: {JsonSerializer.Serialize(value)}\n\n";
    private static HttpResponseMessage Sse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
    private static async Task<List<ModelUpdate>> Collect(IAsyncEnumerable<ModelUpdate> stream)
    { List<ModelUpdate> result = []; await foreach (var update in stream) result.Add(update); return result; }

    private sealed class FixtureHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<JsonElement> Bodies { get; } = [];
        public Uri? LastUri { get; private set; }
        public string? ApiKey { get; private set; }
        public string? Version { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUri = request.RequestUri;
            ApiKey = request.Headers.TryGetValues("x-api-key", out var keys) ? keys.Single() : request.Headers.Authorization?.Parameter;
            Version = request.Headers.TryGetValues("anthropic-version", out var versions) ? versions.Single() : null;
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Bodies.Add(document.RootElement.Clone());
            return response();
        }
    }

    private sealed class StalledStream : Stream
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { Reading.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
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
}
