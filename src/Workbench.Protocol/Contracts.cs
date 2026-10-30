using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
public sealed record AgentSession(SessionInfo Info, IReadOnlyList<ChatMessage> Messages, CompactionCheckpoint? Compaction = null);
public sealed record AgentEvent(string SessionId, string RunId, string Type, string? Text = null, ToolCall? Call = null, ToolResult? Result = null, string? ApprovalId = null, long Sequence = 0, DateTimeOffset? Timestamp = null);
public sealed record ApprovalRequest(string Id, string SessionId, string RunId, string ToolName, string ArgumentsJson, string Reason);
public enum ExecutionMode { ReadOnly, WorkspaceWrite, FullAccess }
public sealed record RunOptions(int MaxIterations = 20, int TimeoutSeconds = 600, bool AutoApproveWrites = false, int MaxContextCharacters = 100000, string? RunId = null,
    bool AutoCompact = true, ExecutionMode ExecutionMode = ExecutionMode.WorkspaceWrite, ResearchOptions? Research = null,
    IReadOnlyList<string>? SecretEnvironmentVariables = null);
public sealed record ResearchOptions(bool CompressionGuidance = false, bool RepositoryMemory = false, bool ReadOnlyPrefetch = false,
    string? RepositoryBaseCommit = null, string? CompressionGuidancePath = null, int MaxPrefetches = 4);
public enum ProviderKind { Auto, OpenAiCompatible, DeepSeek, Anthropic, OpenAiResponses }
public sealed record ProviderSettings(string Endpoint = "https://api.deepseek.com", string Model = "deepseek-flash", string ApiKeyEnvironmentVariable = "DEEPSEEK_API_KEY", int MaxOutputTokens = 4096,
    ProviderKind Provider = ProviderKind.Auto, string ReasoningEffort = "none", int? ThinkingBudgetTokens = null, bool SupportsImages = false);
public sealed record ModelUpdate(string? TextDelta = null, IReadOnlyList<ToolCall>? ToolCalls = null, long? InputTokens = null, long? OutputTokens = null,
    ModelContinuationState? Continuation = null, long? CachedInputTokens = null, long? CacheWriteTokens = null, long? ReasoningTokens = null);
public sealed record ToolExecutionContext(string WorkspacePath, Func<string, CancellationToken, ValueTask>? OnOutput = null,
    string? SessionId = null, string? RunId = null, IReadOnlyList<string>? ExcludedEnvironmentVariables = null);
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
public interface ISessionStore
{
    Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken = default);
    Task<AgentSession?> LoadAsync(string id, CancellationToken cancellationToken = default);
    Task SaveAsync(AgentSession session, CancellationToken cancellationToken = default);
    Task<AgentEvent> AppendEventAsync(AgentEvent evt, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentEvent>> EventsAsync(string sessionId, long afterSequence = 0, CancellationToken cancellationToken = default);
}
