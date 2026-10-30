using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using OpenAI.Responses;
using Workbench.Protocol;
using Message = Workbench.Protocol.ChatMessage;

#pragma warning disable OPENAI001 // Pinned Responses SDK surface; serialized requests and terminal events are contract-tested.

namespace Workbench.Core;

/// <summary>
/// Native Responses inference only: all tools and context remain owned by AgentEngine.
/// Stateless item/namespace replay and completed reasoning backfill adapt Pi's Responses adapter;
/// see Licenses/Responses/PROVENANCE.md. The official .NET SDK owns HTTP and streaming parsing.
/// </summary>
public sealed class OpenAiResponsesModel : IAgentModel
{
    public const string OfficialEndpoint = "https://api.openai.com/v1";
    internal const string ContinuationFormat = "openai-responses-v1";
    internal const string ToolNamespace = "cogito";
    private const int MaxItems = 128;
    private const int MaxCalls = 64;
    private const int MaxCharacters = 1_000_000;
    private const int MaxContinuationCharacters = 2_000_000;
    private const int MaxStreamBytes = 12_000_000;
    private readonly ProviderSettings _settings;
    private readonly Func<CancellationToken, Task<string>> _credential;
    private readonly string? _credentialScope;
    private readonly bool _oauth;
    private readonly HttpClient? _http;

    public OpenAiResponsesModel(ProviderSettings settings, string apiKey, HttpClient? httpClient = null)
        : this(settings, _ => Task.FromResult(apiKey), null, false, httpClient)
    { ArgumentException.ThrowIfNullOrWhiteSpace(apiKey); }

    public OpenAiResponsesModel(ProviderSettings settings, Func<CancellationToken, Task<string>> accessTokenProvider,
        string credentialScope, HttpClient? httpClient = null)
        : this(settings, accessTokenProvider, credentialScope, true, httpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(credentialScope);
        if (credentialScope.Length > 256) throw new ArgumentException("Account binding exceeds the supported size.", nameof(credentialScope));
    }

    private OpenAiResponsesModel(ProviderSettings settings, Func<CancellationToken, Task<string>> credential,
        string? scope, bool oauth, HttpClient? http)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Model);
        var endpoint = ModelCapabilities.NormalizeEndpoint(settings.Endpoint);
        if (oauth && endpoint != OfficialEndpoint)
            throw new ArgumentException("ChatGPT authorization can only be sent to the official public Responses API.");
        if (ModelCapabilities.ResolveProvider(settings) is ProviderKind.DeepSeek or ProviderKind.Anthropic)
            throw new ArgumentException("Use this provider's native model adapter.");
        if (!oauth && (settings.MaxOutputTokens is < 1 or > 131072)) throw new ArgumentOutOfRangeException(nameof(settings.MaxOutputTokens));
        if (settings.ThinkingBudgetTokens is not null) throw new ArgumentException("Responses supports reasoning effort, not a manual thinking token budget.");
        if (settings.ReasoningEffort is not ("none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max"))
            throw new ArgumentException("Unsupported Responses reasoning effort.");
        _settings = settings with { Endpoint = endpoint };
        _credential = credential;
        _credentialScope = scope;
        _oauth = oauth;
        _http = http;
    }

    public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ModelCapabilities.ValidateImages(messages, _settings);
        var options = CreateOptions(messages, tools);
        // The caller's auth service refreshes before each request, not by retrying an already-running inference.
        var token = await _credential(cancellationToken);
        if (string.IsNullOrWhiteSpace(token)) throw new ModelRequestException("No usable model credential is available. Sign in again.");
        var client = new ResponsesClient(new ApiKeyCredential(token), new ResponsesClientOptions
        {
            Endpoint = new Uri(_settings.Endpoint + "/"), RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
            NetworkTimeout = TimeSpan.FromMinutes(5),
            Transport = _http is null ? ModelHttpTransport.Shared : new HttpClientPipelineTransport(_http)
        });
        Dictionary<int, Slot> slots = [];
        StringBuilder streamedText = new();
        var latestTextIndex = -1;
        ResponseResult? completed = null;
        var streamedCharacters = 0;
        var argumentCharacters = 0;
        var streamBytes = 0;
        var events = 0;
        await using var stream = client.CreateResponseStreamingAsync(options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (await MoveNextSafelyAsync(stream, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var update = stream.Current;
            if (completed is not null) throw new InvalidDataException("Responses sent data after completion; no tool calls were committed.");
            var eventBytes = ModelReaderWriter.Write(update).ToMemory().Length;
            if (++events > 50_000 || eventBytes > 4_000_000 || eventBytes > MaxStreamBytes - streamBytes)
                throw new InvalidDataException("Responses stream exceeds the response size limit.");
            streamBytes += eventBytes;
            switch (update)
            {
                case StreamingResponseOutputItemAddedUpdate added:
                    Register(added.OutputIndex, added.Item, false);
                    break;
                case StreamingResponseOutputItemDoneUpdate done:
                    Register(done.OutputIndex, done.Item, true);
                    break;
                case StreamingResponseOutputTextDeltaUpdate text:
                    if (text.OutputIndex < 0 || string.IsNullOrEmpty(text.ItemId)) throw new InvalidDataException("Invalid Responses text item identity.");
                    if (!slots.TryGetValue(text.OutputIndex, out var textSlot) || textSlot.Item.Id != text.ItemId || textSlot.Done || textSlot.Item is not MessageResponseItem)
                        throw new InvalidDataException("Responses text is not attached to an active message item.");
                    if (text.OutputIndex < latestTextIndex) throw new InvalidDataException("Responses interleaved visible output items out of order.");
                    latestTextIndex = text.OutputIndex;
                    if (text.Delta.Length > MaxCharacters - streamedCharacters) throw new InvalidDataException("Model text exceeds the response size limit.");
                    streamedCharacters += text.Delta.Length;
                    streamedText.Append(text.Delta);
                    if (text.Delta.Length > 0) yield return new(TextDelta: text.Delta);
                    break;
                case StreamingResponseFunctionCallArgumentsDeltaUpdate arguments:
                    CheckArgumentItem(arguments.OutputIndex, arguments.ItemId);
                    var part = arguments.Delta.ToString();
                    if (part.Length > MaxCharacters - argumentCharacters) throw new InvalidDataException("Model arguments exceed the response size limit.");
                    argumentCharacters += part.Length;
                    slots[arguments.OutputIndex].Arguments.Append(part);
                    break;
                case StreamingResponseFunctionCallArgumentsDoneUpdate argumentsDone:
                    CheckArgumentItem(argumentsDone.OutputIndex, argumentsDone.ItemId);
                    var argumentSlot = slots[argumentsDone.OutputIndex];
                    if (((FunctionCallResponseItem)argumentSlot.Item).FunctionName != argumentsDone.FunctionName ||
                        argumentSlot.Arguments.Length > 0 && argumentSlot.Arguments.ToString() != argumentsDone.FunctionArguments.ToString())
                        throw new InvalidDataException("Responses changed streamed function arguments or identity.");
                    break;
                case StreamingResponseFailedUpdate failed:
                    throw ResponseFailure(failed.Response.Error?.Code.ToString());
                case StreamingResponseIncompleteUpdate:
                    throw new InvalidDataException("Responses ended incomplete; no tool calls or continuation were committed.");
                case StreamingResponseErrorUpdate error:
                    throw ResponseFailure(error.Code);
                case StreamingResponseCompletedUpdate terminal:
                    if (terminal.Response.Status != ResponseStatus.Completed || terminal.Response.Error is not null)
                        throw new InvalidDataException("Responses completion event did not contain a successful response.");
                    completed = terminal.Response;
                    break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (completed is null) throw new InvalidDataException("Responses stream ended without response.completed; no tool calls were committed.");
        if (slots.Values.Any(slot => slot.Item is FunctionCallResponseItem && !slot.Done))
            throw new InvalidDataException("Responses completed with an unfinished tool call.");
        // Pi backfills encrypted reasoning from the terminal response. Prefer that entire ordered output here,
        // retaining item IDs, namespaces, reasoning ciphertext and assistant phase rather than reconstructing them.
        var output = completed.OutputItems.Count > 0 ? completed.OutputItems.ToArray() : slots.OrderBy(pair => pair.Key).Select(pair => pair.Value.Item).ToArray();
        if (output.Length is 0 or > MaxItems) throw new InvalidDataException("Responses returned no usable output or too many output items.");
        var rawItems = output.Select(Raw).ToArray();
        var (visible, calls) = InspectOutput(rawItems);
        foreach (var (index, slot) in slots)
        {
            if (index >= output.Length || output[index].Id != slot.Item.Id || output[index].Kind != slot.Item.Kind)
                throw new InvalidDataException("Responses completion changed output item identities.");
            if (slot.Item is FunctionCallResponseItem)
            {
                if (!slot.Done || !JsonElement.DeepEquals(Raw(slot.Item), rawItems[index]))
                    throw new InvalidDataException("Responses completion changed a completed tool call.");
            }
        }
        // A terminal-only function call is not allowed to bypass output_item.done validation.
        foreach (var call in calls)
            if (!slots.Values.Any(slot => slot.Done && slot.Item is FunctionCallResponseItem function && function.CallId == call.Id))
                throw new InvalidDataException("Responses provided a tool call without a completed item event.");
        var streamed = streamedText.ToString();
        if (!visible.StartsWith(streamed, StringComparison.Ordinal))
            throw new InvalidDataException("Responses completion changed streamed visible text.");
        if (visible.Length > streamed.Length) yield return new(TextDelta: visible[streamed.Length..]);
        var payload = Wire.Element(new { credentialScope = _credentialScope, outputItems = rawItems });
        if (payload.GetRawText().Length > MaxContinuationCharacters) throw new InvalidDataException("Responses continuation exceeds the size limit.");
        var usage = completed.Usage;
        yield return new(ToolCalls: calls, InputTokens: usage?.InputTokenCount, OutputTokens: usage?.OutputTokenCount,
            CachedInputTokens: usage?.InputTokenDetails?.CachedTokenCount, CacheWriteTokens: usage?.InputTokenDetails?.CacheWriteTokenCount,
            ReasoningTokens: usage?.OutputTokenDetails?.ReasoningTokenCount,
            Continuation: new(ContinuationFormat, _settings.Endpoint, _settings.Model, payload));

        void CheckArgumentItem(int index, string itemId)
        {
            if (!slots.TryGetValue(index, out var slot) || slot.Done || slot.Item is not FunctionCallResponseItem || slot.Item.Id != itemId)
                throw new InvalidDataException("Responses arguments are not attached to an active function item.");
        }
        void Register(int index, ResponseItem item, bool done)
        {
            if (index is < 0 or >= MaxItems || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 256)
                throw new InvalidDataException("Invalid Responses output item identity.");
            if (item is not (MessageResponseItem or ReasoningResponseItem or FunctionCallResponseItem))
                throw new InvalidDataException("Responses attempted an unsupported server-side tool or output item.");
            if (slots.TryGetValue(index, out var slot))
            {
                if (slot.Done || !done || slot.Item.Id != item.Id || slot.Item.Kind != item.Kind)
                    throw new InvalidDataException("Responses changed or repeated an output item identity.");
                if (slot.Item is FunctionCallResponseItem previous && item is FunctionCallResponseItem next &&
                    (previous.CallId != next.CallId || previous.FunctionName != next.FunctionName ||
                     slot.Arguments.Length > 0 && slot.Arguments.ToString() != next.FunctionArguments.ToString()))
                    throw new InvalidDataException("Responses changed function identity or streamed arguments.");
                slot.Item = item;
                slot.Done = done;
            }
            else
            {
                if (slots.Values.Any(value => value.Item.Id == item.Id)) throw new InvalidDataException("Duplicate Responses output item ID.");
                slots.Add(index, new(item, done));
            }
        }
    }

    private CreateResponseOptions CreateOptions(IReadOnlyList<Message> messages, IReadOnlyList<ToolDefinition> tools)
    {
        CreateResponseOptions options = new()
        {
            Model = _settings.Model, StoredOutputEnabled = false, StreamingEnabled = true, ParallelToolCallsEnabled = false,
            MaxOutputTokenCount = _oauth ? null : _settings.MaxOutputTokens
        };
        options.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
        if (_settings.ReasoningEffort != "none") options.ReasoningOptions = new() { ReasoningEffortLevel = new(_settings.ReasoningEffort) };
        List<JsonElement> inputs = [];
        HashSet<string> pending = [];
        foreach (var message in messages)
        {
            if (message.Role != "tool" && pending.Count > 0) throw new InvalidDataException("Conversation contains an incomplete tool group.");
            if (message.Continuation is { } continuation)
            {
                if (message.Role != "assistant") throw new InvalidDataException("Only assistant output can contain Responses continuation.");
                ModelCapabilities.ValidateContinuation(continuation, _settings, ContinuationFormat);
                var data = continuation.Payload;
                if (data.ValueKind != JsonValueKind.Object || data.GetRawText().Length > MaxContinuationCharacters ||
                    !data.TryGetProperty("credentialScope", out var scope) || scope.ValueKind is not (JsonValueKind.Null or JsonValueKind.String) ||
                    scope.GetString() != _credentialScope || !data.TryGetProperty("outputItems", out var output) || output.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Stored Responses continuation belongs to another account or is invalid. Start a new chat.");
                var raw = output.EnumerateArray().Select(item => item.Clone()).ToArray();
                var (text, calls) = InspectOutput(raw);
                if (text != message.Content || !calls.SequenceEqual(message.ToolCalls ?? []))
                    throw new InvalidDataException("Stored Responses output no longer matches its assistant message.");
                inputs.AddRange(raw);
            }
            else
            {
                switch (message.Role)
                {
                    case "system": case "developer": inputs.Add(Raw(ResponseItem.CreateDeveloperMessageItem(message.Content))); break;
                    case "user":
                        List<ResponseContentPart> content = [];
                        if (message.Content.Length > 0) content.Add(ResponseContentPart.CreateInputTextPart(message.Content));
                        foreach (var image in message.Images ?? [])
                            content.Add(ResponseContentPart.CreateInputImagePart(new Uri($"data:{image.MediaType};base64,{image.Base64Data}"), new ResponseImageDetailLevel(image.Detail)));
                        inputs.Add(Raw(ResponseItem.CreateUserMessageItem(content)));
                        break;
                    case "assistant":
                        if (message.Content.Length > 0) inputs.Add(Raw(ResponseItem.CreateAssistantMessageItem(message.Content)));
                        foreach (var call in message.ToolCalls ?? [])
                        {
                            ValidateCall(call);
                            var item = ResponseItem.CreateFunctionCallItem(call.Id, call.Name, BinaryData.FromString(call.ArgumentsJson));
#pragma warning disable SCME0001
                            if (_oauth) item.Patch.Set("$.namespace"u8, ToolNamespace);
#pragma warning restore SCME0001
                            inputs.Add(Raw(item));
                        }
                        break;
                    case "tool":
                        if (message.ToolCallId is null || !pending.Remove(message.ToolCallId)) throw new InvalidDataException("Conversation contains an orphan or duplicate tool result.");
                        inputs.Add(Raw(ResponseItem.CreateFunctionCallOutputItem(message.ToolCallId, message.Content)));
                        break;
                    default: throw new InvalidDataException("Unsupported Responses conversation role.");
                }
            }
            if (message.Role == "assistant")
                foreach (var call in message.ToolCalls ?? [])
                    if (!pending.Add(call.Id)) throw new InvalidDataException("Conversation contains duplicate tool calls.");
        }
        if (pending.Count > 0) throw new InvalidDataException("Conversation contains unfinished tool calls.");
        HashSet<string> names = [];
        foreach (var tool in tools)
        {
            if (!ValidName(tool.Name) || !names.Add(tool.Name) || tool.ParametersJson.Length > 64_000) throw new InvalidDataException("Invalid or duplicate Responses tool definition.");
            using var parameters = JsonDocument.Parse(tool.ParametersJson);
            if (parameters.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Tool schema must be an object.");
            options.Tools.Add(ResponseTool.CreateFunctionTool(tool.Name, BinaryData.FromString(tool.ParametersJson), false, tool.Description));
        }
        // SDK2.14 does not expose namespace tools. Its supported JsonPatch extension serializes these
        // documented Responses fields without introducing another HTTP/SSE implementation.
#pragma warning disable SCME0001
        options.Patch.Set("$.input"u8, BinaryData.FromString(JsonSerializer.Serialize(inputs, Wire.Json)));
        if (_oauth && tools.Count > 0)
        {
            var functions = tools.Select(tool => new { type = "function", name = tool.Name, description = tool.Description,
                parameters = JsonSerializer.Deserialize<JsonElement>(tool.ParametersJson, Wire.Json), strict = false }).ToArray();
            options.Patch.Set("$.tools"u8, BinaryData.FromString(JsonSerializer.Serialize(new[] { new { type = "namespace", name = ToolNamespace,
                description = "Tools executed by Cogitide in the selected project under its execution policy.", tools = functions } }, Wire.Json)));
        }
#pragma warning restore SCME0001
        return options;
    }

    private (string Text, IReadOnlyList<ToolCall> Calls) InspectOutput(IReadOnlyList<JsonElement> items)
    {
        if (items.Count is 0 or > MaxItems) throw new InvalidDataException("Invalid Responses output item count.");
        StringBuilder text = new();
        List<ToolCall> calls = [];
        HashSet<string> ids = [];
        HashSet<string> callIds = [];
        var argumentCharacters = 0;
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Invalid Responses output item.");
            if (!item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()) || id.GetString()!.Length > 256 || !ids.Add(id.GetString()!))
                throw new InvalidDataException("Invalid or duplicate Responses output item ID.");
            if (item.TryGetProperty("status", out var status) && status.ValueKind != JsonValueKind.Null && status.GetString() != "completed")
                throw new InvalidDataException("Responses output item is incomplete.");
            switch (type.GetString())
            {
                case "message":
                    if (item.GetProperty("role").GetString() != "assistant") throw new InvalidDataException("Stored output contains a non-assistant message.");
                    foreach (var part in item.GetProperty("content").EnumerateArray())
                    {
                        var partType = part.GetProperty("type").GetString();
                        if (partType is not ("output_text" or "refusal")) throw new InvalidDataException("Unsupported assistant output content.");
                        text.Append(part.GetProperty(partType == "output_text" ? "text" : "refusal").GetString());
                    }
                    if (text.Length > MaxCharacters) throw new InvalidDataException("Model text exceeds the response size limit.");
                    break;
                case "reasoning":
                    // Ciphertext and optional summaries remain opaque; none is displayed or used as a tool request.
                    if (item.GetRawText().Length > MaxContinuationCharacters) throw new InvalidDataException("Reasoning continuation exceeds the size limit.");
                    break;
                case "function_call":
                    var space = item.TryGetProperty("namespace", out var ns) && ns.ValueKind != JsonValueKind.Null ? ns.GetString() : null;
                    if (_oauth ? space != ToolNamespace : space is not null)
                        throw new InvalidDataException("Responses called an unexpected tool namespace.");
                    var call = new ToolCall(item.GetProperty("call_id").GetString()!, item.GetProperty("name").GetString()!, item.GetProperty("arguments").GetString()!);
                    ValidateCall(call);
                    if (!callIds.Add(call.Id) || calls.Count >= MaxCalls || call.ArgumentsJson.Length > MaxCharacters - argumentCharacters)
                        throw new InvalidDataException("Duplicate tool call or total tool budget exceeded.");
                    argumentCharacters += call.ArgumentsJson.Length;
                    calls.Add(call);
                    break;
                default: throw new InvalidDataException("Responses returned an unsupported server-side tool or output item.");
            }
        }
        return (text.ToString(), calls);
    }

    private static void ValidateCall(ToolCall call)
    {
        if (string.IsNullOrWhiteSpace(call.Id) || call.Id.Length > 256 || !ValidName(call.Name) || call.ArgumentsJson is null || call.ArgumentsJson.Length > MaxCharacters)
            throw new InvalidDataException("Invalid Responses tool identity or arguments.");
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        if (arguments.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Tool arguments must be a JSON object.");
    }
    private static bool ValidName(string? value) => value is { Length: > 0 and <= 64 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static JsonElement Raw<T>(T value) => JsonSerializer.Deserialize<JsonElement>(ModelReaderWriter.Write(value).ToString(), Wire.Json);
    private static ModelRequestException ResponseFailure(string? code) => new(code switch
    {
        "subscription_sharing_usage_limit_exceeded" or "subscription_sharing_workplace_individual_usage_limit_exceeded" => "ChatGPT plan usage limit reached. Review account usage or select an API provider; this request was not repeated.",
        "subscription_sharing_usage_unavailable" => "ChatGPT plan usage is unavailable for this account or workspace. Review the sign-in permissions.",
        _ => "The Responses service reported a failed request. No tools or incomplete continuation were committed."
    });
    private static async ValueTask<bool> MoveNextSafelyAsync(IAsyncEnumerator<StreamingResponseUpdate> stream, CancellationToken token)
    {
        try { return await stream.MoveNextAsync(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new ModelRequestException("The model connection timed out. Review recorded state before retrying."); }
        catch (ClientResultException error) { throw ModelRequestException.ForStatus(error.Status); }
        catch (Exception error) when (error is IOException or HttpRequestException)
        { throw new ModelRequestException("The model connection failed or was interrupted. No request was automatically repeated."); }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException)
        { throw new ModelRequestException("The model service returned an invalid Responses stream. No incomplete calls were executed."); }
    }
    private sealed class Slot(ResponseItem item, bool done)
    {
        internal ResponseItem Item { get; set; } = item;
        internal bool Done { get; set; } = done;
        internal StringBuilder Arguments { get; } = new();
    }
}
