using System.Net;
using System.Text;
using System.Text.Json;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class ModelCapabilityTests
{
    private const string Endpoint = "https://api.deepseek.com";
    private static readonly ProviderSettings Thinking = new(Endpoint, "deepseek-flash", ReasoningEffort: "high");
    private static readonly ToolDefinition Read = new("read_file", "Read", "{\"type\":\"object\",\"properties\":{}}");
    private static readonly ImageAttachment Png = new("image/png", "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jp1sAAAAASUVORK5CYII=");

    [Fact]
    public async Task ThinkingStateRoundTripsAllAssistantTurnsAndRemainsPrivate()
    {
        var handler = new FixtureHandler(
            Chunk(new { reasoning_content = "private " }) + Chunk(new { reasoning_content = "analysis" }) +
            Chunk(new { tool_calls = new[] { new { index = 0, id = "read-1", type = "function", function = new { name = "read_file", arguments = "{}" } } } }) +
            Chunk(new { }, "tool_calls") + Usage() + Done,
            Chunk(new { reasoning_content = "second private" }) + Chunk(new { content = "public answer" }) + Chunk(new { }, "stop") + Done,
            Chunk(new { content = "follow-up" }) + Chunk(new { }, "stop") + Done);
        using var http = new HttpClient(handler);
        var model = new OpenAiCompatibleModel(Thinking, "fixture-key", http);
        var first = await Collect(model.StreamAsync([new("user", "read")], [Read]));
        var state = Assert.Single(first, x => x.Continuation is not null).Continuation!;
        Assert.All(first, update => Assert.DoesNotContain("private", update.TextDelta ?? ""));
        Assert.Equal("private analysis", state.Payload.GetProperty("reasoning_content").GetString());
        Assert.Equal(Endpoint, state.Endpoint);
        var firstFinal = first[^1];
        Assert.Equal(128L, firstFinal.InputTokens);
        Assert.Equal(20L, firstFinal.OutputTokens);
        Assert.Equal(96L, firstFinal.CachedInputTokens);
        Assert.Equal(16L, firstFinal.ReasoningTokens);
        ChatMessage[] history = [new("user", "read"), new("assistant", "", firstFinal.ToolCalls, Continuation: state), new("tool", "file", ToolCallId: "read-1")];
        var second = await Collect(model.StreamAsync(history, [Read]));
        Assert.Equal("public answer", string.Concat(second.Select(x => x.TextDelta)));
        await Collect(model.StreamAsync([.. history, new("assistant", "public answer", Continuation: second[^1].Continuation), new("user", "next")], [Read]));
        using var request = JsonDocument.Parse(handler.Bodies[2]);
        var turns = request.RootElement.GetProperty("messages");
        Assert.Equal("private analysis", turns[1].GetProperty("reasoning_content").GetString());
        Assert.Equal("second private", turns[3].GetProperty("reasoning_content").GetString());
        Assert.Equal("enabled", request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("high", request.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(request.RootElement.TryGetProperty("max_completion_tokens", out _));
    }

    [Fact]
    public async Task PiCompatibilityBackfillsEmptyReasoningOnLegacyAssistantMessages()
    {
        using var handler = new FixtureHandler(Chunk(new { content = "done" }, "stop") + Done);
        using var http = new HttpClient(handler);
        var model = new OpenAiCompatibleModel(Thinking, "fixture-key", http);
        await Collect(model.StreamAsync([new("user", "first"), new("assistant", "answer"), new("user", "next")], [Read]));
        using var request = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("", request.RootElement.GetProperty("messages")[1].GetProperty("reasoning_content").GetString());
    }

    [Theory]
    [InlineData("none", "disabled")]
    [InlineData("low", "enabled")]
    [InlineData("high", "enabled")]
    [InlineData("max", "enabled")]
    public async Task DeepSeekEffortSelectionUsesProviderFields(string effort, string type)
    {
        using var handler = new FixtureHandler(Chunk(new { content = "done" }, "stop") + Done);
        using var http = new HttpClient(handler);
        var model = new OpenAiCompatibleModel(Thinking with { ReasoningEffort = effort }, "fixture-key", http);
        await Collect(model.StreamAsync([new("user", "go")], []));
        using var request = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(type, request.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(effort != "none", request.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Theory]
    [InlineData("endpoint")]
    [InlineData("model")]
    [InlineData("format")]
    [InlineData("role")]
    [InlineData("payload")]
    public async Task IncompatibleContinuationIsRejectedBeforeNetwork(string mutation)
    {
        using var handler = new FixtureHandler(Chunk(new { }, "stop") + Done);
        using var http = new HttpClient(handler);
        var state = new ModelContinuationState("deepseek-reasoning-content-v1", Endpoint, "deepseek-flash", Wire.Element(new { reasoning_content = "private" }));
        state = mutation switch
        {
            "endpoint" => state with { Endpoint = "https://other.example/v1" },
            "model" => state with { Model = "deepseek-v4-pro" },
            "format" => state with { Format = "anthropic-blocks-v1" },
            "payload" => state with { Payload = Wire.Element(new { reasoning_content = 42 }) },
            _ => state
        };
        var model = new OpenAiCompatibleModel(Thinking, "fixture-key", http);
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(model.StreamAsync([new(mutation == "role" ? "user" : "assistant", "public", Continuation: state)], [])));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("length")]
    [InlineData("late")]
    [InlineData("oversize")]
    [InlineData("invalid-type")]
    public async Task InvalidStreamCannotCommitReasoning(string failure)
    {
        var response = failure switch
        {
            "length" => Chunk(new { reasoning_content = "private" }) + Chunk(new { }, "length") + Done,
            "late" => Chunk(new { }, "stop") + Chunk(new { reasoning_content = "private" }) + Done,
            "oversize" => Chunk(new { reasoning_content = new string('s', 1_000_001) }) + Chunk(new { }, "stop") + Done,
            "invalid-type" => Chunk(new { reasoning_content = new[] { "private" } }) + Chunk(new { }, "stop") + Done,
            _ => Chunk(new { reasoning_content = "private" })
        };
        using var handler = new FixtureHandler(response);
        using var http = new HttpClient(handler);
        var model = new OpenAiCompatibleModel(Thinking, "fixture-key", http);
        List<ModelUpdate> updates = [];
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var item in model.StreamAsync([new("user", "go")], [])) updates.Add(item);
        });
        Assert.All(updates, item => Assert.Null(item.Continuation));
        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task ExplicitLocalDeepSeekProviderUsesSameProtocolAndEndpointScope()
    {
        using var handler = new FixtureHandler(Chunk(new { reasoning_content = "state" }, "stop") + Done);
        using var http = new HttpClient(handler);
        var settings = Thinking with { Endpoint = "http://localhost:9000/v1/", Provider = ProviderKind.DeepSeek };
        var model = new OpenAiCompatibleModel(settings, "fixture-key", http);
        var result = await Collect(model.StreamAsync([new("user", "go")], []));
        Assert.Equal("http://localhost:9000/v1", result[^1].Continuation!.Endpoint);
    }

    [Fact]
    public async Task GenericSdkUsesNativeReasoningAndImageContentPartsWhenEnabled()
    {
        using var handler = new FixtureHandler(Chunk(new { content = "image answer" }, "stop") + Done);
        using var http = new HttpClient(handler);
        var settings = new ProviderSettings("https://models.example/v1", "vision-model", ReasoningEffort: "medium", SupportsImages: true);
        var model = new OpenAiCompatibleModel(settings, "fixture-key", http);
        await Collect(model.StreamAsync([new("user", "describe", Images: [Png])], []));
        using var request = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("medium", request.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.False(request.RootElement.TryGetProperty("thinking", out _));
        var content = request.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("describe", content[0].GetProperty("text").GetString());
        Assert.Equal("data:image/png;base64," + Png.Base64Data, content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("auto", content[1].GetProperty("image_url").GetProperty("detail").GetString());
    }

    [Fact]
    public async Task DeepSeekFlashAcceptsVisionWithoutGenericOptIn()
    {
        using var handler = new FixtureHandler(Chunk(new { content = "image answer" }, "stop") + Done);
        using var http = new HttpClient(handler);
        await Collect(new OpenAiCompatibleModel(Thinking, "fixture-key", http).StreamAsync([new("user", "describe", Images: [Png])], []));
        Assert.Single(handler.Bodies);
        using var request = JsonDocument.Parse(handler.Bodies[0]);
        var url = request.RootElement.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/png;base64,", url);
        Assert.Equal(Convert.FromBase64String(Png.Base64Data), Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]));
    }

    [Fact]
    public async Task GenericProviderCannotReceiveOpaqueDeepSeekContinuation()
    {
        using var handler = new FixtureHandler(Chunk(new { }, "stop") + Done);
        using var http = new HttpClient(handler);
        var state = new ModelContinuationState("deepseek-reasoning-content-v1", Endpoint, "deepseek-flash", Wire.Element(new { reasoning_content = "private" }));
        var model = new OpenAiCompatibleModel(new("https://models.example/v1", "other"), "fixture-key", http);
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(model.StreamAsync([new("assistant", "answer", Continuation: state)], [])));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task ImageBudgetAppliesAcrossHistoricalMessages()
    {
        using var handler = new FixtureHandler(Chunk(new { }, "stop") + Done);
        using var http = new HttpClient(handler);
        var model = new OpenAiCompatibleModel(Thinking, "fixture-key", http);
        ChatMessage[] history = [new("user", "first", Images: [Png, Png, Png]), new("assistant", "answer"), new("user", "next", Images: [Png, Png])];
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(model.StreamAsync(history, [])));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData("https://api.deepseek.com", "deepseek-v4-pro", true)]
    [InlineData("https://models.example/v1", "unknown", false)]
    public async Task UnsupportedImagesAreRejectedBeforeNetwork(string endpoint, string name, bool optIn)
    {
        using var handler = new FixtureHandler(Chunk(new { }, "stop") + Done);
        using var http = new HttpClient(handler);
        var model = new OpenAiCompatibleModel(new(endpoint, name, SupportsImages: optIn), "fixture-key", http);
        await Assert.ThrowsAsync<InvalidDataException>(() => Collect(model.StreamAsync([new("user", "describe", Images: [Png])], [])));
        Assert.Empty(handler.Bodies);
    }

    [Theory]
    [InlineData("https://api.deepseek.com", ProviderKind.DeepSeek)]
    [InlineData("https://api.anthropic.com", ProviderKind.Anthropic)]
    [InlineData("https://api.deepseek.com.attacker.example", ProviderKind.OpenAiCompatible)]
    public void AutoDetectionUsesExactHost(string endpoint, ProviderKind provider) =>
        Assert.Equal(provider, ModelCapabilities.ResolveProvider(new(endpoint)));

    private const string Done = "data: [DONE]\n\n";
    private static string Chunk(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(new
    {
        id = "completion", @object = "chat.completion.chunk", created = 1, model = "deepseek-flash",
        choices = new[] { new { index = 0, delta, finish_reason = finish } }
    }) + "\n\n";
    private static string Usage() => "data: " + JsonSerializer.Serialize(new
    {
        id = "completion", @object = "chat.completion.chunk", created = 1, model = "deepseek-flash", choices = Array.Empty<object>(),
        usage = new { prompt_tokens = 128, completion_tokens = 20, total_tokens = 148, prompt_cache_hit_tokens = 96, completion_tokens_details = new { reasoning_tokens = 16 } }
    }) + "\n\n";
    private static async Task<List<ModelUpdate>> Collect(IAsyncEnumerable<ModelUpdate> source)
    { List<ModelUpdate> result = []; await foreach (var item in source) result.Add(item); return result; }
    private sealed class FixtureHandler(params string[] responses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new(HttpStatusCode.OK) { Content = new StringContent(responses[Bodies.Count - 1], Encoding.UTF8, "text/event-stream") };
        }
    }
}
