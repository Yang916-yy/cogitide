using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;
using Workbench.Protocol;
using Message = Workbench.Protocol.ChatMessage;

namespace Workbench.Core;

/// <summary>Official SDK transport; tool calls are exposed only after a complete, valid model response.</summary>
public sealed class OpenAiCompatibleModel : IAgentModel
{
    private readonly ChatClient _client;
    private readonly ProviderSettings _settings;
    private readonly bool _deepSeek;
    private readonly bool _thinking;
    private const string ContinuationFormat = "deepseek-reasoning-content-v1";
    private const int MaxToolCalls = 64;
    private const int MaxArgumentCharacters = 1_000_000;
    private const int MaxReasoningCharacters = 1_000_000;

    public OpenAiCompatibleModel(ProviderSettings settings, string apiKey) : this(settings, apiKey, null) { }

    public OpenAiCompatibleModel(ProviderSettings settings, string apiKey, HttpClient? httpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Model);
        var endpoint = ModelCapabilities.NormalizeEndpoint(settings.Endpoint);
        var provider = ModelCapabilities.ResolveProvider(settings);
        if (provider == ProviderKind.Anthropic) throw new ArgumentException("Use the native Anthropic adapter for this provider.");
        _deepSeek = provider == ProviderKind.DeepSeek;
        if (settings.MaxOutputTokens < 1 || settings.MaxOutputTokens > (_deepSeek ? 393216 : 131072))
            throw new ArgumentOutOfRangeException(nameof(settings.MaxOutputTokens));
        if (settings.ThinkingBudgetTokens is not null) throw new ArgumentException("This adapter supports reasoning effort, not a thinking token budget.");
        if (_deepSeek ? settings.ReasoningEffort is not ("none" or "low" or "high" or "max") :
            settings.ReasoningEffort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh"))
            throw new ArgumentException("Unsupported reasoning effort for the selected provider.");
        _settings = settings;
        _thinking = settings.ReasoningEffort != "none";
        var options = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint + "/"),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            NetworkTimeout = TimeSpan.FromMinutes(5),
            Transport = httpClient is null ? ModelHttpTransport.Shared : new HttpClientPipelineTransport(httpClient)
        };
        _client = new ChatClient(settings.Model, new ApiKeyCredential(apiKey), options);
    }

    public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ModelCapabilities.ValidateImages(messages, _settings);
        ChatCompletionOptions options = new() { MaxOutputTokenCount = _deepSeek ? null : _settings.MaxOutputTokens, AllowParallelToolCalls = false };
        if (_deepSeek)
        {
            // Keep older configurations non-thinking. DeepSeek otherwise enables thinking by default.
#pragma warning disable SCME0001 // Pinned SDK extension point; covered by serialized-request contract tests.
            options.Patch.Set("$.thinking"u8, BinaryData.FromString(_thinking ? "{\"type\":\"enabled\"}" : "{\"type\":\"disabled\"}"));
            if (_thinking) options.Patch.Set("$.reasoning_effort"u8, _settings.ReasoningEffort);
            options.Patch.Set("$.max_tokens"u8, _settings.MaxOutputTokens);
#pragma warning restore SCME0001
        }
        else if (_thinking)
        {
#pragma warning disable OPENAI001 // Pinned SDK native reasoning field; serialized request covered by tests.
            options.ReasoningEffortLevel = new ChatReasoningEffortLevel(_settings.ReasoningEffort);
#pragma warning restore OPENAI001
        }
        foreach (var tool in tools)
            options.Tools.Add(ChatTool.CreateFunctionTool(tool.Name, tool.Description, BinaryData.FromString(tool.ParametersJson)));

        var requests = messages.Select(ToSdkMessage).ToList();
        SortedDictionary<int, ToolBuilder> pending = [];
        ChatFinishReason? finish = null;
        long? input = null, output = null, cachedInput = null, reasoningTokens = null;
        StringBuilder reasoning = new();
        var hasReasoning = false;
        var argumentCharacters = 0;
        await using var stream = _client.CompleteChatStreamingAsync(requests, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (await MoveNextSafelyAsync(stream, cancellationToken))
        {
            var update = stream.Current;
            cancellationToken.ThrowIfCancellationRequested();
            var reasoningDelta = _deepSeek ? ReadReasoningDelta(update) : null;
            if (finish is not null && (update.ToolCallUpdates.Count > 0 || update.ContentUpdate.Any(part => !string.IsNullOrEmpty(part.Text)) ||
                !string.IsNullOrEmpty(reasoningDelta) ||
                (update.FinishReason is { } laterFinish && laterFinish != finish)))
                throw new InvalidDataException("Model sent additional content after its completion marker; no tools were executed.");
            if (reasoningDelta is not null)
            {
                hasReasoning = true;
                if (reasoningDelta.Length > MaxReasoningCharacters - reasoning.Length)
                    throw new InvalidDataException("Model continuation exceeds the response size limit.");
                reasoning.Append(reasoningDelta);
            }
            foreach (var part in update.ContentUpdate)
                if (!string.IsNullOrEmpty(part.Text)) yield return new ModelUpdate(TextDelta: part.Text);
            foreach (var delta in update.ToolCallUpdates)
            {
                if (delta.Index < 0) throw new InvalidDataException("Model tool call index must be nonnegative.");
                if (!pending.TryGetValue(delta.Index, out var builder))
                {
                    if (pending.Count >= MaxToolCalls) throw new InvalidDataException("Model response contains too many tool calls.");
                    pending.Add(delta.Index, builder = new());
                }
                if (!string.IsNullOrEmpty(delta.ToolCallId))
                {
                    if (builder.Id is not null && builder.Id != delta.ToolCallId)
                        throw new InvalidDataException("Model changed a tool call ID within the same stream index.");
                    builder.Id = delta.ToolCallId;
                }
                if (!string.IsNullOrEmpty(delta.FunctionName)) builder.Name.Append(delta.FunctionName);
                if (builder.Id?.Length > 256 || builder.Name.Length > 256) throw new InvalidDataException("Model tool identifier exceeds the size limit.");
                if (delta.FunctionArgumentsUpdate is not null)
                {
                    var fragment = delta.FunctionArgumentsUpdate.ToString();
                    argumentCharacters += fragment.Length;
                    if (argumentCharacters > MaxArgumentCharacters) throw new InvalidDataException("Model tool arguments exceed the total response size limit.");
                    builder.Arguments.Append(fragment);
                }
            }
            if (update.FinishReason is { } reason) finish = reason;
            if (update.Usage is { } usage)
            {
                input = usage.InputTokenCount;
                output = usage.OutputTokenCount;
                cachedInput = usage.InputTokenDetails?.CachedTokenCount ?? (_deepSeek ? ReadDeepSeekCacheHits(update) : null);
                reasoningTokens = usage.OutputTokenDetails?.ReasoningTokenCount;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (finish is null) throw new InvalidDataException("The model stream ended without a completion marker; no tools were executed.");
        if (finish != ChatFinishReason.Stop && finish != ChatFinishReason.ToolCalls)
            throw new InvalidDataException($"Model response did not finish normally ({finish}); no tools were executed.");
        if (finish == ChatFinishReason.ToolCalls && pending.Count == 0)
            throw new InvalidDataException("Model declared tool calls without providing a complete call.");
        if (pending.Count > 0 && finish != ChatFinishReason.ToolCalls)
            throw new InvalidDataException("Model returned tool data without a tool-call completion marker; no tools were executed.");
        List<ToolCall> calls = [];
        HashSet<string> callIds = [];
        foreach (var item in pending.Values)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || item.Name.Length == 0 || !callIds.Add(item.Id))
                throw new InvalidDataException("Incomplete or duplicate model tool call.");
            var json = item.Arguments.Length == 0 ? "{}" : item.Arguments.ToString();
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Tool arguments must be an object.");
            calls.Add(new ToolCall(item.Id, item.Name.ToString(), json));
        }
        // Opaque replay data is committed with the valid response, never emitted as visible text or on a partial stream.
        ModelContinuationState? continuation = _deepSeek && (hasReasoning || _thinking)
            ? new(ContinuationFormat, ModelCapabilities.NormalizeEndpoint(_settings.Endpoint), _settings.Model,
                Wire.Element(new { reasoning_content = reasoning.ToString() }))
            : null;
        yield return new ModelUpdate(ToolCalls: calls, InputTokens: input, OutputTokens: output, Continuation: continuation,
            CachedInputTokens: cachedInput, ReasoningTokens: reasoningTokens);
    }

    private static async ValueTask<bool> MoveNextSafelyAsync(IAsyncEnumerator<StreamingChatCompletionUpdate> stream, CancellationToken cancellationToken)
    {
        try { return await stream.MoveNextAsync(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new ModelRequestException("The model connection timed out. Review the recorded state before trying again."); }
        catch (ClientResultException ex) { throw ModelRequestException.ForStatus(ex.Status); }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        { throw new ModelRequestException("The model connection failed or was interrupted. No incomplete tool calls were executed; the stream was not automatically replayed."); }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        { throw new ModelRequestException("The model service returned an invalid stream. No incomplete tool calls were executed."); }
    }

    private OpenAI.Chat.ChatMessage ToSdkMessage(Message message)
    {
        if (message.Continuation is { } state)
        {
            if (!_deepSeek || message.Role != "assistant") throw new InvalidDataException("Stored continuation cannot be replayed by this model or message role.");
            ModelCapabilities.ValidateContinuation(state, _settings, ContinuationFormat);
        }
        switch (message.Role)
        {
            case "system": return new SystemChatMessage(message.Content);
            case "user":
                if (message.Images is not { Count: > 0 }) return new UserChatMessage(message.Content);
                List<ChatMessageContentPart> parts = [];
                if (!string.IsNullOrEmpty(message.Content)) parts.Add(ChatMessageContentPart.CreateTextPart(message.Content));
                foreach (var image in message.Images)
                    parts.Add(ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(Convert.FromBase64String(image.Base64Data)),
                        image.MediaType, new ChatImageDetailLevel(image.Detail)));
                return new UserChatMessage(parts);
            case "assistant":
                var assistant = new AssistantChatMessage(message.Content);
                foreach (var call in message.ToolCalls ?? [])
                    assistant.ToolCalls.Add(ChatToolCall.CreateFunctionToolCall(call.Id, call.Name, BinaryData.FromString(call.ArgumentsJson)));
                if (_deepSeek)
                {
                    // Port of Pi's missing-reasoning backfill rule. Includes non-tool assistant turns; see DEPENDENCIES.md.
                    var replay = "";
                    if (message.Continuation is { } continuation)
                    {
                        if (continuation.Payload.ValueKind != JsonValueKind.Object ||
                            !continuation.Payload.TryGetProperty("reasoning_content", out var value) || value.ValueKind != JsonValueKind.String)
                            throw new InvalidDataException("Stored DeepSeek continuation is invalid.");
                        replay = value.GetString()!;
                        if (replay.Length > MaxReasoningCharacters) throw new InvalidDataException("Stored model continuation exceeds the size limit.");
                    }
#pragma warning disable SCME0001
                    // Explicit JSON handles empty strings too; SCM 1.15's string patch writer rejects an empty encoded value.
                    assistant.Patch.Set("$.reasoning_content"u8, BinaryData.FromString(JsonSerializer.Serialize(replay, Wire.Json)));
#pragma warning restore SCME0001
                }
                return assistant;
            case "tool":
                if (string.IsNullOrWhiteSpace(message.ToolCallId)) throw new InvalidDataException("Tool message is missing its call ID.");
                return new ToolChatMessage(message.ToolCallId, message.Content);
            default: throw new InvalidDataException($"Unsupported message role: {message.Role}.");
        }
    }

    private static string? ReadReasoningDelta(StreamingChatCompletionUpdate update)
    {
#pragma warning disable SCME0001
        try
        {
            // Contains only checks locally recorded edits; TryGetJson also follows SDK child-model propagators.
            if (!update.Patch.TryGetJson("$.choices[0].delta.reasoning_content"u8, out var json)) return null;
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.String => document.RootElement.GetString(),
                JsonValueKind.Null => null,
                _ => throw new InvalidDataException("The model returned invalid continuation data.")
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        { throw new InvalidDataException("The model returned invalid continuation data."); }
#pragma warning restore SCME0001
    }

    private static long? ReadDeepSeekCacheHits(StreamingChatCompletionUpdate update)
    {
#pragma warning disable SCME0001
        return update.Patch.TryGetValue("$.usage.prompt_cache_hit_tokens"u8, out long count) && count >= 0 ? count : null;
#pragma warning restore SCME0001
    }

    private sealed class ToolBuilder
    {
        public string? Id { get; set; }
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
    }
}
