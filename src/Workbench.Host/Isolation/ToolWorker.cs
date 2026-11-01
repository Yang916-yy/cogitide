using System.Diagnostics;
using System.Text;
using StreamJsonRpc;
using Workbench.Protocol;
using Workbench.Tools;

namespace Workbench.Host.Isolation;

/// <summary>Restricted stdio tool RPC target. It is launched only inside a native MXC container by the Host broker.</summary>
public static class ToolWorker
{
    public static async Task<int> RunAsync(string[] args)
    {
        var stage = "arguments";
        try
        {
            if (args is not ["--tool-worker", "--workspace", var workspace, "--session", var session, "--scratch", var scratch] ||
                string.IsNullOrWhiteSpace(session) || session.Length > 256)
                throw new ArgumentException("Tool worker requires workspace, chat ownership and private scratch arguments.");
            stage = "initialize_tools";
            await using var dispatcher = new ToolWorkerDispatcher(workspace, session, scratch, CommandWorkerStart);
            stage = "initialize_rpc";
            using var rpc = new JsonRpc(BoundedRpcPipes.CreateHandler(Console.OpenStandardOutput(), Console.OpenStandardInput(), new SystemTextJsonFormatter()));
            rpc.AddLocalRpcTarget<IToolWorkerRpc>(dispatcher, new JsonRpcTargetOptions());
            rpc.StartListening();
            stage = "serve";
            // Parent stream loss always ends owned commands and integration processes. There is no reconnect/replay.
            try { await rpc.Completion; }
            catch (Exception ex) when (ex is IOException or ConnectionLostException) { }
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Tool worker failed: {ex.GetType().Name} (0x{ex.HResult:X8}, stage={stage}).");
            return 1;
        }
    }

    private static ProcessStartInfo CommandWorkerStart()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Worker executable is unavailable.");
        var start = new ProcessStartInfo(executable);
        if (Path.GetFileName(executable) is not ("Workbench.Host" or "Workbench.Host.exe"))
            start.ArgumentList.Add(typeof(ToolWorker).Assembly.Location);
        start.ArgumentList.Add("--command-worker");
        return start;
    }
}

/// <summary>Reuses the same workspace/command/browser/LSP/MCP adapters; no model or account assemblies are instantiated.</summary>
internal interface IToolWorkerRpc
{
    [JsonRpcMethod(IsolationMethods.Catalog, UseSingleObjectParameterDeserialization = true)]
    Task<IsolationCatalog> CatalogAsync(IsolationCatalogRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod(IsolationMethods.Execute, UseSingleObjectParameterDeserialization = true)]
    Task<ToolResult> ExecuteAsync(IsolationExecuteRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod(IsolationMethods.ReadBytes, UseSingleObjectParameterDeserialization = true)]
    Task<IsolationReadBytesResult> ReadBytesAsync(IsolationReadBytesRequest request, CancellationToken cancellationToken);
    [JsonRpcMethod(IsolationMethods.EndRun, UseSingleObjectParameterDeserialization = true)]
    Task<int> EndRunAsync(IsolationEndRunRequest request);
    [JsonRpcMethod(IsolationMethods.EndSession)]
    Task<int> EndSessionAsync();
}

internal sealed class ToolWorkerDispatcher : IAsyncDisposable, IToolWorkerRpc
{
    private readonly string _workspace;
    private readonly string _session;
    private readonly string _scratch;
    private readonly CommandSessionManager _commands;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<IAgentToolSet> _integrations = [];
    private Dictionary<string, IAgentTool> _tools = new(StringComparer.Ordinal);
    private string? _run;
    private ExecutionMode _executionMode;
    private bool _disposed;

    internal ToolWorkerDispatcher(string workspace, string session, string scratch, Func<ProcessStartInfo> commandWorker)
    {
        _workspace = WorkspaceService.Root(workspace); _session = session; _scratch = WorkspaceService.Root(scratch);
        _commands = new(Path.Combine(_scratch, "commands"), commandWorker);
    }

    [JsonRpcMethod(IsolationMethods.Catalog, UseSingleObjectParameterDeserialization = true)]
    public async Task<IsolationCatalog> CatalogAsync(IsolationCatalogRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.RunId) || request.RunId.Length > 256) throw new ArgumentException("A bounded run identifier is required.");
        if (!Enum.IsDefined(request.ExecutionMode)) throw new ArgumentException("Unknown execution mode.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run is not null && _run != request.RunId) throw new InvalidOperationException("End the previous run before creating another tool catalog.");
            if (_run == request.RunId) return new(_tools.Values.Select(t => t.Definition).ToArray());
            var allowExternal = request.AllowExternalServers && request.ExecutionMode != ExecutionMode.ReadOnly;
            var lsp = allowExternal
                ? await LspToolSet.LoadAsync(Path.Combine(_scratch, "lsp.json"), _workspace, ct: _shutdown.Token)
                : new LspToolSet(_workspace, new([]));
            _integrations.Add(lsp);
            // Worktree administration intentionally stays outside this grant: sibling checkouts
            // cannot be silently added to an existing worker's filesystem policy.
            var tools = WorkspaceTools.Create(new(), _commands, lsp)
                .Where(t => t.Definition.Name is not ("create_worktree" or "list_worktrees" or "worktree_status" or "remove_worktree")).ToList();
            tools.Add(new BrowserCheckTool(Path.Combine(_scratch, "browser-artifacts")));
            if (allowExternal && File.Exists(Path.Combine(_scratch, "mcp.json")))
            {
                var mcp = await McpToolSet.ConnectAsync(Path.Combine(_scratch, "mcp.json"), _workspace, ct: _shutdown.Token);
                _integrations.Add(mcp);
                tools.AddRange(mcp.Tools);
            }
            _tools = tools.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);
            _run = request.RunId;
            _executionMode = request.ExecutionMode;
            return new(tools.Select(t => t.Definition).ToArray());
        }
        catch
        {
            await DisposeIntegrationsAsync();
            throw;
        }
        finally { _gate.Release(); }
    }

    [JsonRpcMethod(IsolationMethods.Execute, UseSingleObjectParameterDeserialization = true)]
    public async Task<ToolResult> ExecuteAsync(IsolationExecuteRequest request, CancellationToken cancellationToken)
    {
        if (request.Call is null || request.Call.ArgumentsJson is null || request.Call.ArgumentsJson.Length > 4 * 1024 * 1024)
            throw new ArgumentException("Tool arguments exceed the worker budget.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_run != request.RunId || !_tools.TryGetValue(request.Call.Name, out var tool))
                throw new InvalidOperationException("Tool call does not belong to an initialized run catalog.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            var context = new ToolExecutionContext(_workspace, SessionId: _session, RunId: request.RunId, RequireOsIsolation: true, ExecutionMode: _executionMode);
            return await tool.ExecuteAsync(request.Call, context, linked.Token);
        }
        finally { _gate.Release(); }
    }

    [JsonRpcMethod(IsolationMethods.ReadBytes, UseSingleObjectParameterDeserialization = true)]
    public async Task<IsolationReadBytesResult> ReadBytesAsync(IsolationReadBytesRequest request, CancellationToken cancellationToken)
    {
        if (_run is null || request.RunId != _run) throw new InvalidOperationException("Read does not belong to the active worker run.");
        if (request.MaximumBytes is < 1 or > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(request.MaximumBytes));
        var path = WorkspaceService.Resolve(_workspace, request.Path);
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            var buffer = new byte[request.MaximumBytes + 1];
            var count = await file.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken);
            if (count > request.MaximumBytes) throw new InvalidDataException("Workspace instructions exceed the byte budget.");
            return new(buffer.AsSpan(0, count).ToArray());
        }
        catch (FileNotFoundException) { return new([], Missing: true); }
        catch (DirectoryNotFoundException) { return new([], Missing: true); }
    }

    [JsonRpcMethod(IsolationMethods.EndRun, UseSingleObjectParameterDeserialization = true)]
    public async Task<int> EndRunAsync(IsolationEndRunRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            var unfinished = await _commands.EndRunAsync(_session, request.RunId);
            if (_run == request.RunId) { await DisposeIntegrationsAsync(); _tools.Clear(); _run = null; }
            return unfinished;
        }
        finally { _gate.Release(); }
    }

    [JsonRpcMethod(IsolationMethods.EndSession)]
    public async Task<int> EndSessionAsync() { _shutdown.Cancel(); return await _commands.EndSessionAsync(_session); }

    private async Task DisposeIntegrationsAsync()
    {
        foreach (var integration in _integrations.AsEnumerable().Reverse()) await integration.DisposeAsync();
        _integrations.Clear();
    }
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _gate.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            try { await _commands.DisposeAsync(); }
            finally { await DisposeIntegrationsAsync(); }
        }
        finally { _gate.Release(); }
        _shutdown.Dispose();
    }
}
