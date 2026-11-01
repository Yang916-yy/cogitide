using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Runtime.CompilerServices;

namespace Workbench.Protocol;

public static class Wire
{
    public const int Version = 1;
    public static JsonSerializerOptions Json { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        // Stable wire/storage contracts use generated metadata; anonymous tool/RPC payloads retain compatibility.
        TypeInfoResolver = JsonTypeInfoResolver.Combine(WireJsonContext.Default, new DefaultJsonTypeInfoResolver()),
        Converters = { new JsonStringEnumConverter() }
    };
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
}

public sealed record RpcRequest(string Id, string Method, JsonElement Params);
public sealed record RpcReply(string Id, JsonElement? Result = null, string? Error = null);
public sealed record RpcNotification(string Method, JsonElement Params);

public sealed record ToolCall(string Id, string Name, string ArgumentsJson);
public sealed record ImageAttachment(string MediaType, string Base64Data, string? Name = null, string Detail = "auto");
// Provider-specific assistant state is opaque to the UI. It may only be replayed to its original endpoint/model.
public sealed record ModelContinuationState(string Format, string Endpoint, string Model, JsonElement Payload);
public sealed record ChatMessage(string Role, string Content, IReadOnlyList<ToolCall>? ToolCalls = null, string? ToolCallId = null,
    IReadOnlyList<ImageAttachment>? Images = null, ModelContinuationState? Continuation = null, bool? ToolIsError = null);
public sealed record ToolDefinition(string Name, string Description, string ParametersJson, bool RequiresApproval = false);
public sealed record ToolResult(string Content, bool IsError = false, string? ArtifactPath = null);
public sealed record SessionInfo(string Id, string Title, string WorkspacePath, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string Status = "idle", string Backend = "api", string? ExternalThreadId = null,
    string? AccountBinding = null);
public sealed record AgentSession(SessionInfo Info, IReadOnlyList<ChatMessage> Messages, CompactionCheckpoint? Compaction = null,
    RunCheckpoint? RunCheckpoint = null);
public sealed record RunUsage(long InputTokens = 0, long OutputTokens = 0, int ModelCalls = 0, int CallsWithUnknownUsage = 0,
    int IncompleteCalls = 0)
{
    public long ReportedTokens => checked(InputTokens + OutputTokens);
}
public sealed record RunCheckpoint(int Version, string RunId, int ActiveStartMessageIndex, RunOptions Options,
    int IterationsCompleted, DateTimeOffset StartedAt, string? StopReason = null, RunUsage? Usage = null, string? ParentRunId = null,
    long LastAppliedInputSequence = 0);
public sealed record PendingRunInput(long Sequence, string RunId, string Text);
public sealed record PendingInputPage(IReadOnlyList<PendingRunInput> Items, int TotalPending, bool HasMore,
    bool Available = true, string? UnavailableReason = null, bool LegacyRecovery = false);
public sealed record SessionView(SessionInfo Info, IReadOnlyList<ChatMessage> Messages, CompactionCheckpoint? Compaction,
    int TotalMessages, int FirstMessageIndex, bool HasEarlierMessages, RunCheckpoint? RunCheckpoint = null,
    bool Available = true, string? UnavailableReason = null, PendingInputPage? PendingInputs = null, bool OmittedPayloads = false,
    long? MessageEventSequence = null);
public sealed record SessionMessagePage(IReadOnlyList<ChatMessage> Messages, int FirstMessageIndex, int TotalMessages,
    bool HasEarlierMessages, bool Available = true, string? UnavailableReason = null, bool OmittedPayloads = false);
public sealed record SessionEventPage(IReadOnlyList<AgentEvent> Items, long? NextBeforeSequence, bool HasMore,
    bool Available = true, string? UnavailableReason = null);
public sealed record SessionEventReplayPage(IReadOnlyList<AgentEvent> Items, long NextAfterSequence, long ThroughSequence,
    bool HasMore, bool Available = true, string? UnavailableReason = null);
public sealed record AgentEvent(string SessionId, string RunId, string Type, string? Text = null, ToolCall? Call = null, ToolResult? Result = null, string? ApprovalId = null, long Sequence = 0, DateTimeOffset? Timestamp = null);
public sealed record ApprovalRequest(string Id, string SessionId, string RunId, string ToolName, string ArgumentsJson, string Reason);
public enum ExecutionMode { ReadOnly, WorkspaceWrite, FullAccess }
public sealed record RunOptions(int MaxIterations = 256, int TimeoutSeconds = 21600, bool AutoApproveWrites = false, int MaxContextCharacters = 100000, string? RunId = null,
    bool AutoCompact = true, ExecutionMode ExecutionMode = ExecutionMode.WorkspaceWrite, ResearchOptions? Research = null,
    IReadOnlyList<string>? SecretEnvironmentVariables = null, long? MaxReportedTokens = null, bool RequireOsIsolation = false);
public static class RunLimits
{
    public const int MaximumIterations = 10_000;
    public const int MaximumTimeoutSeconds = 604_800;
    public static void Validate(RunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.ExecutionMode)) throw new ArgumentException("Unknown execution mode.", nameof(options));
        if (options.MaxIterations is < 1 or > MaximumIterations || options.TimeoutSeconds is < 1 or > MaximumTimeoutSeconds ||
            options.MaxContextCharacters is < 4000 or > 1_000_000 || options.MaxReportedTokens is < 1 or > 1_000_000_000_000)
            throw new ArgumentException("Run limits are outside supported bounds.", nameof(options));
        var research = options.Research ?? new();
        if (research.MaxPrefetches is < 0 or > 8 || research.RepositoryBaseCommit?.Length > 128 || research.CompressionGuidancePath?.Length > 4096)
            throw new ArgumentException("Research settings exceed supported bounds.", nameof(options));
    }
    public static void ValidateContinuation(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.RunCheckpoint is not { Version: 1 } previous || previous.Options is null || string.IsNullOrWhiteSpace(previous.RunId) ||
            previous.IterationsCompleted < 0 || previous.LastAppliedInputSequence < 0 || previous.ActiveStartMessageIndex < 0 ||
            previous.ActiveStartMessageIndex >= session.Messages.Count || session.Messages[previous.ActiveStartMessageIndex].Role != "user" ||
            session.Info.Status is not ("paused" or "cancelled" or "interrupted" or "failed"))
            throw new InvalidOperationException("This session has no valid stopped task checkpoint to continue. Review its history and start a new task explicitly.");
        try { Validate(previous.Options); }
        catch (ArgumentException ex) { throw new InvalidOperationException("The saved task checkpoint has invalid options; review its history and start a new task explicitly.", ex); }
        if (previous.IterationsCompleted > previous.Options.MaxIterations || previous.Usage is { } usage &&
            (usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.InputTokens > long.MaxValue - usage.OutputTokens ||
             usage.ModelCalls < 0 || usage.CallsWithUnknownUsage < 0 || usage.IncompleteCalls < 0 ||
             usage.CallsWithUnknownUsage > usage.ModelCalls || usage.IncompleteCalls > usage.ModelCalls))
            throw new InvalidOperationException("The saved task checkpoint has invalid progress or usage; review its original evidence before continuing.");
    }
}
public sealed record ResearchOptions(bool CompressionGuidance = false, bool RepositoryMemory = false, bool ReadOnlyPrefetch = false,
    string? RepositoryBaseCommit = null, string? CompressionGuidancePath = null, int MaxPrefetches = 4);
public enum ProviderKind { Auto, OpenAiCompatible, DeepSeek, Anthropic, OpenAiResponses }
public sealed record ProviderSettings(string Endpoint = "https://api.deepseek.com", string Model = "deepseek-flash", string ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY", int MaxOutputTokens = 4096,
    ProviderKind Provider = ProviderKind.Auto, string ReasoningEffort = "none", int? ThinkingBudgetTokens = null, bool SupportsImages = false,
    string? CredentialProfileId = null);
public sealed record ModelUpdate(string? TextDelta = null, IReadOnlyList<ToolCall>? ToolCalls = null, long? InputTokens = null, long? OutputTokens = null,
    ModelContinuationState? Continuation = null, long? CachedInputTokens = null, long? CacheWriteTokens = null, long? ReasoningTokens = null);
public sealed record ToolExecutionContext(string WorkspacePath, Func<string, CancellationToken, ValueTask>? OnOutput = null,
    string? SessionId = null, string? RunId = null, IReadOnlyList<string>? ExcludedEnvironmentVariables = null,
    bool RequireOsIsolation = false, ExecutionMode ExecutionMode = ExecutionMode.WorkspaceWrite);
public sealed record AccountState(bool Available, bool LoggedIn, string? Email = null, string? Plan = null, string? Error = null,
    string? AccountBinding = null);
public sealed record AccountProfile(string AccountBinding, string? Email = null, string? Label = null, bool IsSelected = false, bool LoggedIn = false);
public sealed record LoginInfo(string? AuthUrl, string? LoginId, string? UserCode = null);
public sealed record ModelInfo(string Id, string Name, IReadOnlyList<string>? ReasoningEfforts = null, string? DefaultReasoningEffort = null, bool? SupportsImages = null);

public interface IAgentModel
{
    IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken cancellationToken = default);
}
public interface IAgentTool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default);
}
// Multi-file tools expose paths from their validated operation parser, not from a second parser in the engine.
public interface IWorkspacePathTool
{
    IReadOnlyList<string> GetPaths(ToolCall call, string workspace);
}
public interface IRunResourceLifetime
{
    // Stops owned resources before a run releases its workspace. Returns the number that were still active.
    Task<int> EndRunAsync(string sessionId, string runId);
}
public interface IAgentToolSet : IAsyncDisposable
{
    IReadOnlyList<IAgentTool> Tools { get; }
}
public interface ISessionStore
{
    Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken = default);
    Task<AgentSession?> LoadAsync(string id, CancellationToken cancellationToken = default);
    Task SaveAsync(AgentSession session, CancellationToken cancellationToken = default);
    Task<AgentEvent> AppendEventAsync(AgentEvent evt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentEvent>> EventsAsync(string sessionId, long afterSequence = 0, CancellationToken cancellationToken = default);
    async IAsyncEnumerable<AgentEvent> StreamEventsAsync(string sessionId, long afterSequence = 0, bool descending = false,
        string? eventType = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        var events = await EventsAsync(sessionId, afterSequence, cancellationToken);
        foreach (var evt in descending ? events.Reverse() : events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (eventType is null || evt.Type == eventType) yield return evt;
        }
    }
    async Task<AgentEvent?> GetEventAsync(string sessionId, long sequence, CancellationToken cancellationToken = default)
    {
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        return (await EventsAsync(sessionId, sequence - 1, cancellationToken)).FirstOrDefault(evt => evt.Sequence == sequence);
    }
    async Task<SessionEventPage> EventsPageAsync(string sessionId, long? beforeSequence = null, int limit = 100, CancellationToken cancellationToken = default) =>
        SessionHistoryPages.Events(await EventsAsync(sessionId, 0, cancellationToken), beforeSequence, limit);
    async Task<SessionMessagePage?> MessagesPageAsync(string id, int? beforeIndex = null, int limit = 100, CancellationToken cancellationToken = default) =>
        await LoadAsync(id, cancellationToken) is { } session ? SessionHistoryPages.Messages(session.Messages, beforeIndex, limit) : null;
    async Task<SessionView?> ReadViewAsync(string id, int messageLimit = 100, CancellationToken cancellationToken = default)
    {
        if (await LoadAsync(id, cancellationToken) is not { } session) return null;
        var page = SessionHistoryPages.Messages(session.Messages, null, messageLimit);
        return new(session.Info, page.Messages, session.Compaction, page.TotalMessages, page.FirstMessageIndex,
            page.HasEarlierMessages, session.RunCheckpoint, page.Available, page.UnavailableReason,
            await PendingInputsAsync(id, 128, cancellationToken), page.OmittedPayloads);
    }
    async Task<PendingInputPage> PendingInputsAsync(string id, int limit = 128, CancellationToken cancellationToken = default)
    {
        SessionHistoryPages.ValidateLimit(limit);
        var session = await LoadAsync(id, cancellationToken);
        var events = await EventsAsync(id, 0, cancellationToken);
        var checkpoint = session?.RunCheckpoint;
        var pending = events.Where(evt => evt.Type == "input.queued" &&
            (checkpoint is null || evt.Sequence > checkpoint.LastAppliedInputSequence)).ToList();
        if (checkpoint is null)
            foreach (var applied in events.Where(evt => evt.Type == "input.steered"))
            {
                var index = pending.FindIndex(evt => evt.RunId == applied.RunId && evt.Text == applied.Text);
                if (index >= 0) pending.RemoveAt(index);
            }
        var items = pending.Take(limit).Select(evt => new PendingRunInput(evt.Sequence, evt.RunId, evt.Text ?? "")).ToArray();
        if (JsonSerializer.Serialize(items, Wire.Json).Length > SessionHistoryPages.MaximumPageCharacters)
            return new([], pending.Count, true, false, "Pending instructions exceed the recovery page budget. Inspect exact source events before continuing.", checkpoint is null);
        return new(items, pending.Count, pending.Count > items.Length, LegacyRecovery: checkpoint is null);
    }
}

// Compatibility for small/in-memory adapters. Production SQLite overrides these methods with bounded SQL reads.
public static class SessionHistoryPages
{
    public const int MaximumPageSize = 1000;
    public const int MaximumPageCharacters = 1_000_000;
    public static void ValidateLimit(int limit)
    {
        if (limit is < 1 or > MaximumPageSize) throw new ArgumentOutOfRangeException(nameof(limit));
    }
    public static SessionMessagePage Messages(IReadOnlyList<ChatMessage> source, int? beforeIndex, int limit)
    {
        ValidateLimit(limit);
        var end = beforeIndex ?? source.Count;
        if (end < 0 || end > source.Count) throw new ArgumentOutOfRangeException(nameof(beforeIndex));
        if (end < source.Count && source[end].Role == "tool") throw new ArgumentException("The cursor splits a tool group.", nameof(beforeIndex));
        var start = end;
        List<ChatMessage> result = [];
        var omitted = false;
        long characters = 2;
        while (start > 0 && result.Count < limit)
        {
            var first = start - 1;
            while (first > 0 && source[first].Role == "tool") first--;
            var originals = source.Skip(first).Take(start - first).ToArray();
            var group = originals.Select(message => message with { Continuation = null,
                Images = message.Images?.Select(image => image with { Base64Data = "" }).ToArray() }).ToArray();
            var size = group.Sum(message => JsonSerializer.Serialize(message, Wire.Json).Length + 1L);
            if (characters + size > MaximumPageCharacters)
            {
                if (result.Count == 0) return new([], end, source.Count, end > 0, false,
                    "The next whole message group exceeds the history page character budget. Read the original evidence with an exact source reference.");
                break;
            }
            result.InsertRange(0, group);
            omitted |= originals.Any(message => message.Continuation is not null || message.Images is { Count: > 0 });
            characters += size;
            start = first;
        }
        return new(result, start, source.Count, start > 0, OmittedPayloads: omitted);
    }
    public static SessionEventPage Events(IReadOnlyList<AgentEvent> source, long? beforeSequence, int limit)
    {
        ValidateLimit(limit);
        if (beforeSequence is < 1) throw new ArgumentOutOfRangeException(nameof(beforeSequence));
        var selected = source.Where(evt => beforeSequence is null || evt.Sequence < beforeSequence).OrderByDescending(evt => evt.Sequence).ToArray();
        List<AgentEvent> result = [];
        long size = 2;
        foreach (var evt in selected.Take(limit))
        {
            var next = JsonSerializer.Serialize(evt, Wire.Json).Length + 1L;
            if (size + next > MaximumPageCharacters) break;
            size += next;
            result.Add(evt);
        }
        if (result.Count == 0 && selected.Length > 0)
            return new([], beforeSequence, true, false, "The next event exceeds the history page character budget. Use its exact source event reference.");
        result.Reverse();
        return new(result, result.Count > 0 ? result[0].Sequence : beforeSequence, selected.Length > result.Count);
    }
}
