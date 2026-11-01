using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using StreamJsonRpc;
using Workbench.Host.Isolation;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

/// <summary>Functional dispatcher fixtures run without a sandbox; native denials are a separate explicit fixture.</summary>
public sealed class OsIsolationWorkerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(OsIsolationTestPaths.PhysicalTemporaryDirectory(), "cogitide-worker-fixture-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Combine(_root, "workspace");
    private ToolWorkerDispatcher _worker = null!;
    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Workspace);
        var scratch = Directory.CreateDirectory(Path.Combine(_root, "scratch")).FullName;
        _worker = new(Workspace, "chat", scratch, CommandWorkerStart);
        return Task.CompletedTask;
    }
    public async Task DisposeAsync() { await _worker.DisposeAsync(); Directory.Delete(_root, true); }

    [Fact]
    public async Task LiveRpcExposesOnlyTheWorkerAllowlist()
    {
        // BCL pipes use a Unix socket below the platform temp path; macOS caps it at 104 bytes.
        var name = "cw-" + Guid.NewGuid().ToString("N");
        await using var serverPipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await using var clientPipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Task.WhenAll(serverPipe.WaitForConnectionAsync(deadline.Token), clientPipe.ConnectAsync(deadline.Token));
        using var server = new JsonRpc(new HeaderDelimitedMessageHandler(serverPipe, serverPipe, new SystemTextJsonFormatter()));
        server.AddLocalRpcTarget<IToolWorkerRpc>(_worker, new JsonRpcTargetOptions());
        server.StartListening();
        using var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientPipe, clientPipe, new SystemTextJsonFormatter()));
        client.StartListening();
        var catalog = await client.InvokeWithParameterObjectAsync<IsolationCatalog>(IsolationMethods.Catalog, new IsolationCatalogRequest("rpc-run"), cancellationToken: deadline.Token);
        Assert.Contains(catalog.Tools, t => t.Name == "read_file");
        foreach (var forbidden in new[] { "DisposeAsync", "GetType", "account.status", "run.start", "model.infer" })
            await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => client.InvokeWithParameterObjectAsync<object>(forbidden, argument: (object?)null, cancellationToken: deadline.Token));
    }

    [Fact]
    public async Task CatalogOwnershipRulesAndBoundedReadsStayInsideTheWorker()
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "AGENTS.md"), "worker rules 😀");
        var catalog = await _worker.CatalogAsync(new("run"), default);
        Assert.Contains(catalog.Tools, t => t.Name == "read_file");
        Assert.Contains(catalog.Tools, t => t.Name == "start_command" && t.RequiresApproval);
        Assert.DoesNotContain(catalog.Tools, t => t.Name.Contains("worktree", StringComparison.Ordinal));
        Assert.Equal("worker rules 😀", System.Text.Encoding.UTF8.GetString((await _worker.ReadBytesAsync(new("AGENTS.md", 1024, "run"), default)).Content));
        Assert.True((await _worker.ReadBytesAsync(new("missing.md", 1024, "run"), default)).Missing);
        var bom = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'x' };
        await File.WriteAllBytesAsync(Path.Combine(Workspace, "BOM.md"), bom);
        await File.WriteAllBytesAsync(Path.Combine(Workspace, "empty.md"), []);
        Assert.Equal(bom, (await _worker.ReadBytesAsync(new("BOM.md", 4, "run"), default)).Content);
        var empty = await _worker.ReadBytesAsync(new("empty.md", 4, "run"), default);
        Assert.False(empty.Missing);
        Assert.Empty(empty.Content);
        await Assert.ThrowsAsync<InvalidDataException>(() => _worker.ReadBytesAsync(new("AGENTS.md", 3, "run"), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _worker.ReadBytesAsync(new("../outside", 1024, "run"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _worker.ReadBytesAsync(new("AGENTS.md", 1024, "other-run"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _worker.ExecuteAsync(new(new("1", "read_file", "{\"path\":\"AGENTS.md\"}"), "other-run"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _worker.ExecuteAsync(new(new("1", "account.status", "{}"), "run"), default));
    }

    [Fact]
    public async Task EndRunRetainsExplicitSessionCommandsAndEndSessionStopsThem()
    {
        await _worker.CatalogAsync(new("run"), default);
        var command = OperatingSystem.IsWindows() ? "Write-Output 'owned'; Start-Sleep -Seconds 30" : "printf 'owned\\n'; sleep 30";
        var result = await _worker.ExecuteAsync(new(new("1", "start_command", JsonSerializer.Serialize(new
        { command, timeoutSeconds = 60, yieldMilliseconds = 100, lifetime = "session" })), "run"), default);
        Assert.False(result.IsError, result.Content);
        using var started = JsonDocument.Parse(result.Content);
        var commandId = started.RootElement.GetProperty("commandId").GetString();
        Assert.Equal(0, await _worker.EndRunAsync(new("run")));
        await _worker.CatalogAsync(new("next-run"), default);
        var read = await _worker.ExecuteAsync(new(new("2", "read_command", JsonSerializer.Serialize(new { commandId })), "next-run"), default);
        Assert.False(read.IsError, read.Content);
        using var state = JsonDocument.Parse(read.Content);
        Assert.True(state.RootElement.GetProperty("running").GetBoolean());
        Assert.Equal(1, await _worker.EndSessionAsync());
    }

    private static ProcessStartInfo CommandWorkerStart()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var dotnet = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        var start = new ProcessStartInfo(dotnet);
        start.ArgumentList.Add(typeof(ToolWorker).Assembly.Location);
        start.ArgumentList.Add("--command-worker");
        return start;
    }
}
