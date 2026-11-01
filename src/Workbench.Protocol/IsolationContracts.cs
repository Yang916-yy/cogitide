namespace Workbench.Protocol;

/// <summary>The native worker is a separate OS boundary; application approval modes remain independent.</summary>
public sealed record OsIsolationStatus(bool Supported, string Backend, string? Reason = null,
    bool NetworkDenied = true, bool RequiresNativeLaunchValidation = true);

public interface IAgentToolExecutionBroker
{
    Task<IAgentToolSet?> CreateRunToolsAsync(ToolExecutionContext context, CancellationToken cancellationToken = default);
    Task<ToolResult> ExecuteAsync(IAgentTool tool, ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default);
    Task<string> ReadWorkspaceTextAsync(string workspace, string path, int maximumCharacters, CancellationToken cancellationToken = default);
    Task<byte[]> ReadWorkspaceBytesAsync(string workspace, string path, int maximumBytes, CancellationToken cancellationToken = default);
    Task<byte[]> ReadWorkspaceBytesAsync(ToolExecutionContext context, string path, int maximumBytes, CancellationToken cancellationToken = default) =>
        ReadWorkspaceBytesAsync(context.WorkspacePath, path, maximumBytes, cancellationToken);
}

// Deliberately no inference, account, credential, unrestricted execution, or Host-data RPC.
public static class IsolationMethods
{
    public const string Catalog = "tool.catalog";
    public const string Execute = "tool.execute";
    public const string ReadBytes = "workspace.read_bytes";
    public const string EndRun = "run.end";
    public const string EndSession = "session.end";
}

public sealed record IsolationCatalogRequest(string RunId, bool AllowExternalServers = true, ExecutionMode ExecutionMode = ExecutionMode.WorkspaceWrite);
public sealed record IsolationCatalog(IReadOnlyList<ToolDefinition> Tools);
public sealed record IsolationExecuteRequest(ToolCall Call, string RunId);
public sealed record IsolationReadBytesRequest(string Path, int MaximumBytes, string? RunId = null);
public sealed record IsolationReadBytesResult(byte[] Content, bool Missing = false);
public sealed record IsolationEndRunRequest(string RunId);
