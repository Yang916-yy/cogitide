using Workbench.Protocol;
using Workbench.Tools;

internal sealed partial class WorkbenchHost
{
    private static System.Diagnostics.ProcessStartInfo IsolatedWorkerStart()
    {
        var start = CommandWorkerStart();
        start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
        start.ArgumentList.Add("--tool-worker");
        return start;
    }

    private sealed class RunResources(CommandSessionManager commands, OsIsolationManager isolation) : IRunResourceLifetime
    {
        public async Task<int> EndRunAsync(string sessionId, string runId) =>
            await commands.EndRunAsync(sessionId, runId) + await isolation.EndRunAsync(sessionId, runId);
    }
    private async Task<IAgentToolSet?> LoadIntegrationToolsAsync(string workspace, IReadOnlyList<string>? excluded, CancellationToken ct)
    {
        List<IAgentToolSet> leases = [];
        try
        {
            var lsp = await LspToolSet.LoadAsync(Path.Combine(_directory, "lsp.json"), workspace, excluded, ct);
            leases.Add(lsp);
            if (File.Exists(Path.Combine(_directory, "mcp.json")))
                leases.Add(await McpToolSet.ConnectAsync(Path.Combine(_directory, "mcp.json"), workspace, excluded, ct));
            return new IntegrationTools(leases);
        }
        catch
        {
            foreach (var lease in leases.AsEnumerable().Reverse()) await lease.DisposeAsync();
            throw;
        }
    }

    private async Task<T> WithReviewMutationAsync<T>(string path, Func<string, Task<T>> mutation)
    {
        var workspace = WorkspaceService.Root(path);
        // Review writes require an idle Host, including runs opened through another path spelling.
        // Git still checks snapshot/index identity and its own locks at mutation time.
        var owner = "review:" + Guid.NewGuid().ToString("N");
        lock (_workspaceAdmissionGate)
        {
            if (!_runs.IsEmpty || !_workspaceRuns.IsEmpty)
                throw new InvalidOperationException("Stop active work before changing the Git index, checkout or publication state.");
            if (!_workspaceRuns.TryAdd(workspace, owner)) throw new InvalidOperationException("The workspace is busy.");
        }
        try { return await mutation(workspace); }
        finally { _workspaceRuns.TryRemove(new KeyValuePair<string, string>(workspace, owner)); }
    }

    private sealed class IntegrationTools(IReadOnlyList<IAgentToolSet> leases) : IAgentToolSet
    {
        public IReadOnlyList<IAgentTool> Tools { get; } = leases.SelectMany(lease => lease.Tools).ToArray();
        public async ValueTask DisposeAsync()
        {
            Exception? failure = null;
            foreach (var lease in leases.Reverse())
                try { await lease.DisposeAsync(); } catch (Exception error) { failure ??= error; }
            if (failure is not null) throw new IOException("Integration cleanup was not fully observed.", failure);
        }
    }
}
