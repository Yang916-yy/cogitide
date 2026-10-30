using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Helpers;
using Anthropic.Models.Messages;
using Workbench.Protocol;
using ChatMessage = Workbench.Protocol.ChatMessage;
using ToolCall = Workbench.Protocol.ToolCall;

namespace Workbench.Core;

/// <summary>Native Messages API via Anthropic's official HTTP, SSE and content aggregation implementation.</summary>
public sealed class AnthropicModel : IAgentModel
{
    public const string ContinuationFormat = "anthropic.messages.content.v1";
    private const int MaxStreamCharacters = 8 * 1024 * 1024;
    private const int MaxToolArguments = 1024 * 1024;
    private readonly ProviderSettings _settings;
    private readonly AnthropicClient _client;
    private readonly TimeSpan _requestTimeout;

    public AnthropicModel(ProviderSettings settings, string apiKey, HttpClient? httpClient = null,
        TimeSpan? requestTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Model);
        var endpoint = ModelCapabilities.NormalizeEndpoint(settings.Endpoint);
        _settings = settings with { Endpoint = endpoint };
        _requestTimeout = requestTimeout ?? TimeSpan.FromMinutes(5);
        if (_requestTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        if (settings.MaxOutputTokens <= 0) throw new ArgumentOutOfRangeException(nameof(settings.MaxOutputTokens));
        _ = Thinking();
        _client = new AnthropicClient
        {
            ApiKey = apiKey,
            BaseUrl = endpoint.EndsWith("/v1", StringComparison.Ordinal) ? endpoint[..^3] : endpoint,
            HttpClient = httpClient ?? ModelHttpTransport.SharedClient,
            MaxRetries = 0,
            Timeout = _requestTimeout,
            OpenTelemetry = false
        };
    }

    public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parameters = CreateParameters(messages, tools);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        var guard = new StreamGuard();
        var aggregator = new MessageContentAggregator();
        var source = guard.Validate(_client.Messages.CreateStreaming(parameters, deadline.Token));
        await using var enumerator = aggregator.CollectAsync(source).GetAsyncEnumerator(deadline.Token);
        while (await MoveNext(enumerator, cancellationToken).ConfigureAwait(false))
        {
            var value = enumerator.Current.Value;
            if (value is RawContentBlockStartEvent { ContentBlock.Value: TextBlock start } && start.Text.Length > 0)
                yield return new(TextDelta: start.Text);
            else if (value is RawContentBlockDeltaEvent { Delta.Value: TextDelta delta } && delta.Text.Length > 0)
                yield return new(TextDelta: delta.Text);
        }

        ModelUpdate final;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            var result = aggregator.Message();
            var content = guard.PreserveContent(result.Content);
            var calls = ReadCalls(content);
            var stop = guard.StopReason;
            if (calls.Count > 0 ? stop != "tool_use" : stop is not ("end_turn" or "stop_sequence" or "refusal"))
                throw InvalidStream();
            if (calls.Any(call => !tools.Any(tool => tool.Name == call.Name))) throw InvalidStream();
            var usage = result.Usage;
            var cached = usage.CacheReadInputTokens ?? 0;
            var written = usage.CacheCreationInputTokens ?? 0;
            // Anthropic input_tokens excludes both cached reads and cache writes.
            var input = checked(usage.InputTokens + cached + written);
            if (input < 0 || usage.OutputTokens < 0 || cached < 0 || written < 0) throw InvalidStream();
            final = new(ToolCalls: calls.Count == 0 ? null : calls,
                InputTokens: input, OutputTokens: usage.OutputTokens,
                Continuation: new(ContinuationFormat, _settings.Endpoint, _settings.Model, content),
                CachedInputTokens: cached, CacheWriteTokens: written,
                ReasoningTokens: usage.OutputTokensDetails?.ThinkingTokens);
        }
        catch (Exception exception) when (IsProviderFailure(exception, cancellationToken))
        {
            throw Sanitize(exception);
        }
        yield return final;
    }

    private MessageCreateParams CreateParameters(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools)
    {
        ModelCapabilities.ValidateImages(messages, _settings);
        List<MessageParam> conversation = [];
        List<string> system = [];
        foreach (var message in messages)
        {
            if (message.Role == "system")
            {
                if (message.Continuation is not null) throw new ArgumentException("Only assistant messages may carry model continuation state.");
                system.Add(message.Content);
                continue;
            }
            if (message.Continuation is { } state)
            {
                ModelCapabilities.ValidateContinuation(state, _settings, ContinuationFormat);
                if (message.Role != "assistant") throw new ArgumentException("Only assistant messages may carry model continuation state.");
                ValidateContinuationContent(message, state.Payload);
                conversation.Add(new AssistantMessageParam { Content = new MessageContentParam(state.Payload.EnumerateArray()
                    .Select(block => block.Deserialize<ContentBlockParam>() ?? throw InvalidStream()).ToArray()) });
                continue;
            }
            List<ContentBlockParam> content = [];
            switch (message.Role)
            {
                case "user":
                    foreach (var image in message.Images ?? [])
                        content.Add(new ImageBlockParam { Source = new Base64ImageSource { Data = image.Base64Data, MediaType = image.MediaType } });
                    if (message.Content.Length > 0) content.Add(new TextBlockParam { Text = message.Content });
                    if (content.Count == 0) throw new ArgumentException("A user message must contain text or an image.");
                    conversation.Add(new UserMessageParam { Content = content });
                    break;
                case "assistant":
                    if (message.Content.Length > 0) content.Add(new TextBlockParam { Text = message.Content });
                    foreach (var call in message.ToolCalls ?? [])
                    {
                        using var input = JsonDocument.Parse(call.ArgumentsJson);
                        if (input.RootElement.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool input must be a JSON object.");
                        content.Add(new ToolUseBlockParam { ID = call.Id, Name = call.Name,
                            Input = input.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone()) });
                    }
                    if (content.Count > 0) conversation.Add(new AssistantMessageParam { Content = content });
                    break;
                case "tool":
                    ArgumentException.ThrowIfNullOrWhiteSpace(message.ToolCallId);
                    conversation.Add(new UserMessageParam { Content = new MessageContentParam(new ContentBlockParam[] {
                        new ToolResultBlockParam { ToolUseID = message.ToolCallId, Content = message.Content, IsError = message.ToolIsError } }) });
                    break;
                default: throw new ArgumentException("Unsupported conversation role.");
            }
        }
        if (conversation.Count == 0) throw new ArgumentException("At least one conversation message is required.");
        ValidateToolPairs(messages);
        var thinking = Thinking();
        return new MessageCreateParams
        {
            Model = _settings.Model,
            MaxTokens = _settings.MaxOutputTokens,
            Messages = conversation,
            System = system.Count == 0 ? null : new MessageCreateParamsSystem(string.Join("\n\n", system)),
            Thinking = thinking,
            OutputConfig = _settings.ThinkingBudgetTokens is null && _settings.ReasoningEffort != "none"
                ? new OutputConfig { Effort = _settings.ReasoningEffort } : null,
            Tools = tools.Select(tool => new ToolUnion(new Tool { Name = tool.Name, Description = tool.Description,
                InputSchema = JsonSerializer.Deserialize<InputSchema>(tool.ParametersJson) ?? throw new ArgumentException("Invalid tool schema.") })).ToArray()
        };
    }

    private static void ValidateToolPairs(IReadOnlyList<ChatMessage> messages)
    {
        HashSet<string> pending = [];
        foreach (var message in messages)
        {
            if (message.Role == "system") continue;
            if (message.Role == "tool")
            {
                if (message.ToolCallId is null || !pending.Remove(message.ToolCallId))
                    throw new InvalidDataException("A Claude tool result has no matching pending call.");
                continue;
            }
            if (pending.Count != 0) throw new InvalidDataException("Claude tool calls must have results before the next conversation turn.");
            if (message.Role == "assistant")
                foreach (var call in message.ToolCalls ?? [])
                    if (string.IsNullOrWhiteSpace(call.Id) || !pending.Add(call.Id))
                        throw new InvalidDataException("Claude tool call IDs must be nonempty and unique within a turn.");
        }
        if (pending.Count != 0) throw new InvalidDataException("Claude tool calls must have results before requesting the next response.");
    }

    private ThinkingConfigParam Thinking()
    {
        if (_settings.ThinkingBudgetTokens is { } budget)
        {
            var model = _settings.Model;
            string[] legacyPrefixes = ["claude-3-7-sonnet", "claude-sonnet-4-20250514", "claude-opus-4-20250514",
                "claude-sonnet-4-5", "claude-sonnet-4-6", "claude-opus-4-1", "claude-opus-4-5", "claude-opus-4-6", "claude-haiku-4-5"];
            var legacy = legacyPrefixes.Any(prefix => model == prefix || model.StartsWith(prefix + "-", StringComparison.Ordinal));
            if (!legacy || budget < 1024 || budget >= _settings.MaxOutputTokens)
                throw new ArgumentException("Manual thinking requires a supported legacy Claude model and a budget from 1024 to less than max output tokens.");
            return new ThinkingConfigEnabled { BudgetTokens = budget };
        }
        return _settings.ReasoningEffort switch
        {
            "none" => new ThinkingConfigDisabled(),
            "low" or "medium" or "high" or "xhigh" or "max" => new ThinkingConfigAdaptive(),
            _ => throw new ArgumentException("Unsupported Claude reasoning effort.")
        };
    }

    private static void ValidateContinuationContent(ChatMessage message, JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array || content.GetRawText().Length > MaxStreamCharacters) throw InvalidStream();
        var text = string.Concat(content.EnumerateArray().Where(block => block.GetProperty("type").GetString() == "text")
            .Select(block => block.GetProperty("text").GetString()));
        var calls = ReadCalls(content);
        var expected = message.ToolCalls ?? [];
        if (text != message.Content || calls.Count != expected.Count) throw InvalidStream();
        for (var index = 0; index < calls.Count; index++)
        {
            using var left = JsonDocument.Parse(calls[index].ArgumentsJson);
            using var right = JsonDocument.Parse(expected[index].ArgumentsJson);
            if (calls[index].Id != expected[index].Id || calls[index].Name != expected[index].Name ||
                !JsonElement.DeepEquals(left.RootElement, right.RootElement)) throw InvalidStream();
        }
    }

    private static List<ToolCall> ReadCalls(JsonElement content)
    {
        List<ToolCall> calls = [];
        HashSet<string> ids = [];
        var argumentLength = 0;
        foreach (var block in content.EnumerateArray())
        {
            var kind = block.GetProperty("type").GetString();
            if (kind is not ("text" or "thinking" or "redacted_thinking" or "tool_use")) throw InvalidStream();
            if (kind == "thinking" && string.IsNullOrWhiteSpace(block.GetProperty("signature").GetString())) throw InvalidStream();
            if (kind == "redacted_thinking" && string.IsNullOrWhiteSpace(block.GetProperty("data").GetString())) throw InvalidStream();
            if (kind != "tool_use") continue;
            var id = block.GetProperty("id").GetString();
            var name = block.GetProperty("name").GetString();
            var input = block.GetProperty("input");
            var arguments = input.GetRawText();
            argumentLength += arguments.Length;
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || !ids.Add(id) || string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
                input.ValueKind != JsonValueKind.Object || argumentLength > MaxToolArguments || calls.Count >= 64) throw InvalidStream();
            calls.Add(new(id, name, arguments));
        }
        return calls;
    }

    private static async Task<bool> MoveNext(IAsyncEnumerator<RawMessageStreamEvent> enumerator, CancellationToken cancellationToken)
    {
        try { return await enumerator.MoveNextAsync().ConfigureAwait(false); }
        catch (Exception exception) when (IsProviderFailure(exception, cancellationToken)) { throw Sanitize(exception); }
    }

    private static bool IsProviderFailure(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        AnthropicException or HttpRequestException or IOException or JsonException or InvalidOperationException or ArgumentException or OverflowException or KeyNotFoundException => true,
        _ => false
    };

    private static ModelRequestException Sanitize(Exception exception) => exception switch
    {
        AnthropicApiException api => ModelRequestException.ForStatus((int)api.StatusCode),
        OperationCanceledException => new("The model request timed out. No automatic retry was attempted."),
        _ => new("The Claude response was interrupted or invalid. No tools were executed from the incomplete response.")
    };

    private static ModelRequestException InvalidStream() => new("The Claude response was incomplete or invalid. No tools were executed from this response.");

    // The SDK owns SSE decoding and aggregation. This gate only enforces our execution boundary
    // and bounds; it must run before the aggregator, which intentionally ignores trailing events.
    private sealed class StreamGuard
    {
        private readonly List<JsonElement> _starts = [];
        public string? StopReason { get; private set; }
        public async IAsyncEnumerable<RawMessageStreamEvent> Validate(IAsyncEnumerable<RawMessageStreamEvent> source)
        {
            var started = false;
            var finished = false;
            var messageDeltaSeen = false;
            var activeIndex = -1L;
            string? activeKind = null;
            var characters = 0L;
            var eventCount = 0;
            var argumentCharacters = 0L;
            StringBuilder? toolJson = null;
            await foreach (var item in source.ConfigureAwait(false))
            {
                characters += item.Json.GetRawText().Length;
                if (characters > MaxStreamCharacters || ++eventCount > 100_000 || finished) throw InvalidStream();
                switch (item.Value)
                {
                    case RawMessageStartEvent start:
                        if (started || start.Message.Content.Count != 0) throw InvalidStream();
                        started = true;
                        break;
                    case RawContentBlockStartEvent block:
                        if (!started || messageDeltaSeen || activeIndex >= 0 || block.Index != _starts.Count || _starts.Count >= 128) throw InvalidStream();
                        activeIndex = block.Index;
                        var raw = block.ContentBlock.Json;
                        activeKind = raw.GetProperty("type").GetString();
                        if (activeKind is not ("text" or "thinking" or "redacted_thinking" or "tool_use")) throw InvalidStream();
                        _starts.Add(raw.Clone());
                        toolJson = activeKind == "tool_use" ? new StringBuilder() : null;
                        if (toolJson is not null)
                        {
                            var initialInput = raw.GetProperty("input");
                            if (initialInput.ValueKind != JsonValueKind.Object) throw InvalidStream();
                            argumentCharacters += initialInput.GetRawText().Length;
                            if (argumentCharacters > MaxToolArguments) throw InvalidStream();
                        }
                        break;
                    case RawContentBlockDeltaEvent delta:
                        if (activeIndex < 0 || delta.Index != activeIndex) throw InvalidStream();
                        var valid = (activeKind, delta.Delta.Value) switch
                        {
                            ("text", TextDelta or CitationsDelta) => true,
                            ("thinking", ThinkingDelta or SignatureDelta) => true,
                            ("tool_use", InputJsonDelta) => true,
                            _ => false
                        };
                        if (!valid) throw InvalidStream();
                        if (delta.Delta.Value is InputJsonDelta input)
                        {
                            argumentCharacters += input.PartialJson.Length;
                            if (argumentCharacters > MaxToolArguments) throw InvalidStream();
                            toolJson!.Append(input.PartialJson);
                        }
                        break;
                    case RawContentBlockStopEvent block:
                        if (activeIndex < 0 || block.Index != activeIndex) throw InvalidStream();
                        // SDK aggregation deliberately tolerates partial/malformed tool input by
                        // retaining the start object. That tolerance must not authorize a tool.
                        if (toolJson is { Length: > 0 })
                        {
                            using var toolInput = JsonDocument.Parse(toolJson.ToString());
                            if (toolInput.RootElement.ValueKind != JsonValueKind.Object) throw InvalidStream();
                        }
                        activeIndex = -1;
                        break;
                    case RawMessageDeltaEvent delta:
                        if (!started || activeIndex >= 0) throw InvalidStream();
                        messageDeltaSeen = true;
                        if (delta.Delta.StopReason is { } reason)
                        {
                            if (StopReason is not null && StopReason != reason.Raw()) throw InvalidStream();
                            StopReason = reason.Raw();
                        }
                        break;
                    case RawMessageStopEvent:
                        if (!started || activeIndex >= 0 || StopReason is null) throw InvalidStream();
                        finished = true;
                        break;
                    default:
                        // Ping is normally consumed by the SDK. Unknown transport events carry no authority.
                        if (item.Json.GetProperty("type").GetString() != "ping") throw InvalidStream();
                        break;
                }
                yield return item;
            }
            if (!finished) throw InvalidStream();
        }

        public JsonElement PreserveContent(IReadOnlyList<ContentBlock> blocks)
        {
            if (blocks.Count != _starts.Count) throw InvalidStream();
            JsonArray content = [];
            for (var index = 0; index < blocks.Count; index++)
            {
                var merged = JsonNode.Parse(blocks[index].Json.GetRawText())!.AsObject();
                // The SDK reconstitutes thinking blocks. Keep any additional provider fields opaque.
                foreach (var property in _starts[index].EnumerateObject())
                    if (!merged.ContainsKey(property.Name)) merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                content.Add(merged);
            }
            return JsonSerializer.SerializeToElement(content);
        }
    }
}
