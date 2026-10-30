using System.Net;
using System.Text;
using System.Text.Json;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class ProviderStreamingTests
{
    [Fact]
    public async Task FragmentedCallsAreAssembledAndDeepSeekCompatibilityIsExplicit()
    {
        var stream = Event(new { tool_calls = new[] { new { index = 0, id = "call-1", type = "function", function = new { name = "read_", arguments = "{\"pa" } } } }) +
                     Event(new { tool_calls = new[] { new { index = 0, function = new { name = "file", arguments = "th\":\"文档.cs\"}" } } } }) +
                     Event(new { }, "tool_calls") + "data: [DONE]\n\n";
        var handler = new SseHandler(stream);
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleModel(new("https://api.deepseek.com", "deepseek-flash", MaxOutputTokens: 512), "fixture-key", http);
        var updates = await Collect(provider.StreamAsync([new("user", "read")], [new("read_file", "Read", "{\"type\":\"object\",\"properties\":{}}") ]));
        var call = Assert.Single(updates.SelectMany(u => u.ToolCalls ?? []));
        Assert.Equal("read_file", call.Name);
        Assert.Equal("call-1", call.Id);
        Assert.Equal("文档.cs", JsonDocument.Parse(call.ArgumentsJson).RootElement.GetProperty("path").GetString());
        using var body = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(512, body.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("/chat/completions", handler.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task IncompleteStreamExposesNoToolCall()
    {
        var handler = new SseHandler(Event(new { tool_calls = new[] { new { index = 0, id = "c", type = "function", function = new { name = "write", arguments = "{}" } } } }));
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleModel(new("http://localhost:9000/v1", "test"), "fixture-key", http);
        List<ModelUpdate> received = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var item in provider.StreamAsync([new("user", "go")], [])) received.Add(item);
        });
        Assert.Empty(received.SelectMany(x => x.ToolCalls ?? []));
    }

    [Fact]
    public async Task TruncatedGenerationCannotCommitOtherwiseValidTool()
    {
        var handler = new SseHandler(Event(new { tool_calls = new[] { new { index = 0, id = "c", type = "function", function = new { name = "write", arguments = "{}" } } } }) + Event(new { }, "length") + "data: [DONE]\n\n");
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleModel(new("http://localhost:9000/v1", "test"), "fixture-key", http);
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(provider.StreamAsync([new("user", "go")], [])));
    }

    [Fact]
    public async Task HistoryRoundTripsToolArgumentsAndResultIds()
    {
        var handler = new SseHandler(Event(new { content = "done" }) + Event(new { }, "stop") + "data: [DONE]\n\n");
        using var http = new HttpClient(handler);
        var provider = new OpenAiCompatibleModel(new("http://localhost:9000/v1", "test"), "fixture-key", http);
        var history = new ChatMessage[]
        {
            new("system", "help"), new("user", "go"),
            new("assistant", "", [new("c", "read", "{\"path\":\"x\"}")]),
            new("tool", "file content", ToolCallId: "c")
        };
        var updates = await Collect(provider.StreamAsync(history, []));
        Assert.Equal("done", string.Concat(updates.Select(x => x.TextDelta)));
        using var body = JsonDocument.Parse(handler.RequestBody!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("c", messages[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("c", messages[3].GetProperty("tool_call_id").GetString());
        Assert.False(body.RootElement.TryGetProperty("thinking", out _));
    }

    [Fact]
    public void NonLocalPlainHttpIsRejectedBeforeSendingCredentials()
    {
        Assert.Throws<ArgumentException>(() => new OpenAiCompatibleModel(new("http://example.com/v1", "test"), "fixture-key"));
    }

    [Fact]
    public async Task CancellationReachesHttpTransport()
    {
        using var http = new HttpClient(new BlockingHandler());
        var provider = new OpenAiCompatibleModel(new("http://localhost:9000/v1", "test"), "fixture-key", http);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collect(provider.StreamAsync([new("user", "go")], [], cts.Token)));
    }

    private static string Event(object delta, string? reason = null) => "data: " + JsonSerializer.Serialize(new
    {
        id = "completion", @object = "chat.completion.chunk", created = 1, model = "test",
        choices = new[] { new { index = 0, delta, finish_reason = reason } }
    }) + "\n\n";

    private static async Task<List<ModelUpdate>> Collect(IAsyncEnumerable<ModelUpdate> source)
    { List<ModelUpdate> result = []; await foreach (var item in source) result.Add(item); return result; }

    private sealed class SseHandler(string response) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }
        public Uri? RequestUri { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "text/event-stream") };
        }
    }
    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); throw new InvalidOperationException(); }
    }
}
