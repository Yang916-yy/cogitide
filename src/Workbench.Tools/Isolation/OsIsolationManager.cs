using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using Microsoft.Mxc.Sdk.V1;
using StreamJsonRpc;
using Workbench.Protocol;

namespace Workbench.Tools;

/// <summary>One native sandbox per chat/workspace. An ordinary run lease never destroys session-owned commands.</summary>
public sealed class OsIsolationManager : IAgentToolExecutionBroker, IRunResourceLifetime, IAsyncDisposable
{
    private readonly Func<ProcessStartInfo> _workerStart;
    private readonly Func<string, IReadOnlyList<string>?, CancellationToken, Task<IAgentToolSet?>>? _normalFactory;
    private readonly string _protectedDataDirectory;
    private readonly ConcurrentDictionary<string, Worker> _workers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _starts = new(1, 1);
    private bool _disposed;

    public OsIsolationManager(Func<ProcessStartInfo> workerStart, string protectedDataDirectory,
        Func<string, IReadOnlyList<string>?, CancellationToken, Task<IAgentToolSet?>>? normalFactory = null)
    {
        _workerStart = workerStart;
        _protectedDataDirectory = Path.GetFullPath(protectedDataDirectory);
        _normalFactory = normalFactory;
    }

    public OsIsolationStatus Probe() => OsIsolationCapabilities.Probe();

    public async Task<IAgentToolSet?> CreateRunToolsAsync(ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!context.RequireOsIsolation)
        {
            if (context.SessionId is { } session && _workers.ContainsKey(session)) await EndSessionAsync(session);
            if (context.ExecutionMode == ExecutionMode.ReadOnly) return null;
            return _normalFactory is null ? null : await _normalFactory(context.WorkspacePath, context.ExcludedEnvironmentVariables, cancellationToken);
        }
        ValidateOwner(context);
        var workspace = WorkspaceService.Root(context.WorkspacePath);
        Worker? worker = null;
        await _starts.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var configuration = context.ExecutionMode == ExecutionMode.ReadOnly
                ? new ConfigurationSnapshot(new(StringComparer.Ordinal), "read-only-no-external-servers")
                : await CaptureConfigurationAsync(cancellationToken);
            if (_workers.TryGetValue(context.SessionId!, out var previous) &&
                (previous.ExecutionMode != context.ExecutionMode || previous.ConfigurationDigest != configuration.Digest || !SamePath(previous.Workspace, workspace)))
                await EndSessionAsync(context.SessionId!);
            if (!_workers.TryGetValue(context.SessionId!, out worker))
            {
                var status = Probe();
                if (!status.Supported) throw new PlatformNotSupportedException("OS isolation required: " + status.Reason);
                var scratch = Path.Combine(Path.GetTempPath(), "CogitideIsolation", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(scratch, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                MxcProcess? process = null;
                try
                {
                    // Never write into worker-controlled scratch after launch: an isolated
                    // command could replace a destination with a link into broker state.
                    foreach (var (name, bytes) in configuration.Files)
                        await File.WriteAllBytesAsync(Path.Combine(scratch, name), bytes, cancellationToken);
                    var start = _workerStart();
                    foreach (var argument in new[] { "--workspace", workspace, "--session", context.SessionId!, "--scratch", scratch }) start.ArgumentList.Add(argument);
                    var request = OsIsolationPolicy.Create(start, workspace, scratch, _protectedDataDirectory, context.ExecutionMode);
                    // Do not abandon a native launch when its awaiter is cancelled: acquire ownership,
                    // then terminate the returned container before propagating cancellation.
                    process = await MxcContainer.SpawnAsync(request, new SpawnOptions { Telemetry = new TelemetryConfig { Enabled = false } }, CancellationToken.None);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (process.Warnings.Count != 0)
                        throw new InvalidOperationException("OS isolation required: native launch returned policy warnings; no weaker policy is accepted.");
                    worker = new(process, workspace, scratch, context.ExecutionMode, configuration.Digest);
                    process = null;
                    _workers[context.SessionId!] = worker;
                }
                catch
                {
                    if (process is not null) { try { process.Kill(); } finally { process.Dispose(); } }
                    // Leave private scratch for explicit cleanup if a native launch had an unknown outcome.
                    throw;
                }
            }
            if (!SamePath(worker.Workspace, workspace))
                throw new InvalidOperationException("Stop this chat's owned worker before changing its isolated workspace.");
            worker.ThrowIfLost();
            var catalog = await worker.Rpc.InvokeWithParameterObjectAsync<IsolationCatalog>(IsolationMethods.Catalog,
                new IsolationCatalogRequest(context.RunId!, context.ExecutionMode != ExecutionMode.ReadOnly, context.ExecutionMode), cancellationToken: cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (catalog.Tools.Count > 128 || catalog.Tools.Sum(t => t.ParametersJson.Length + t.Description.Length) > 512 * 1024 ||
                catalog.Tools.Any(t => string.IsNullOrWhiteSpace(t.Name) || t.Name.Length > 128 || t.Name.Contains('.')) ||
                catalog.Tools.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count() != catalog.Tools.Count)
                throw new InvalidDataException("The isolated worker returned an invalid or oversized tool catalog.");
            worker.ActiveRunId = context.RunId;
            var originals = WorkspaceTools.Create(new()).ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);
            var tools = catalog.Tools.Select(definition => (IAgentTool)new ProxyTool(this,
                // External metadata cannot remove the kernel's approval requirement.
                definition with
                {
                    RequiresApproval = originals.TryGetValue(definition.Name, out var original)
                        ? original.Definition.RequiresApproval : definition.Name is not ("read_command" or "stop_command"),
                    Description = definition.Name is "run_command" or "start_command"
                        ? definition.Description.Replace("Not a sandbox. ", "", StringComparison.Ordinal).Replace("Not an OS sandbox.", "", StringComparison.Ordinal)
                            + " Runs in the required native worker; filesystem restrictions and network denial apply."
                        : definition.Description
                },
                originals.GetValueOrDefault(definition.Name) as IWorkspacePathTool)).ToArray();
            return new RunLease(this, worker, context.SessionId!, context.RunId!, tools, cancellationToken);
        }
        catch (Exception ex)
        {
            if (worker is not null)
            {
                await worker.DisposeAsync();
                if (ex is not OperationCanceledException)
                    throw new IOException("The required native worker could not initialize. " + worker.DiagnosticSummary(), ex);
            }
            throw;
        }
        finally { _starts.Release(); }
    }

    public async Task<ToolResult> ExecuteAsync(IAgentTool tool, ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!context.RequireOsIsolation)
        {
            if (tool is ProxyTool) throw new InvalidOperationException("An isolated tool lease cannot be used after isolation is disabled.");
            return await tool.ExecuteAsync(call, context, cancellationToken);
        }
        ValidateOwner(context);
        if (!_workers.TryGetValue(context.SessionId!, out var worker) || !SamePath(worker.Workspace, context.WorkspacePath) ||
            worker.ActiveRunId != context.RunId || worker.ExecutionMode != context.ExecutionMode)
            throw new InvalidOperationException("The required isolated worker was not initialized for this chat and workspace.");
        worker.ThrowIfLost();
        try
        {
            var result = await worker.Rpc.InvokeWithParameterObjectAsync<ToolResult>(IsolationMethods.Execute,
                new IsolationExecuteRequest(call, context.RunId!), cancellationToken: cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(610), cancellationToken);
            // Worker paths are untrusted metadata, never authority for a broker-side file read.
            return result with { ArtifactPath = null };
        }
        catch (OperationCanceledException)
        {
            await worker.DisposeAsync();
            throw;
        }
        catch (Exception ex) when (ex is ConnectionLostException or RemoteInvocationException or IOException or ObjectDisposedException or TimeoutException)
        {
            await worker.DisposeAsync();
            return new("Isolated tool transport failed. The action's outcome is unknown; inspect current workspace evidence before retrying. No action was replayed.", true);
        }
    }

    public async Task<string> ReadWorkspaceTextAsync(string workspace, string path, int maximumCharacters, CancellationToken cancellationToken = default)
    {
        if (maximumCharacters is < 1 or > 262_144) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var bytes = await ReadWorkspaceBytesAsync(workspace, path, maximumCharacters * 4 + 4, cancellationToken);
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        if (text.Length > maximumCharacters) throw new InvalidDataException("Workspace instructions exceed the text budget.");
        return text;
    }

    public async Task<byte[]> ReadWorkspaceBytesAsync(string workspace, string path, int maximumBytes, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var candidates = _workers.Values.Where(w => w.ActiveRunId is not null && SamePath(w.Workspace, workspace)).Take(2).ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException("Workspace-only reads require one unambiguous active worker; provide chat/run context.");
        return await ReadFromWorkerAsync(candidates[0], candidates[0].ActiveRunId!, path, maximumBytes, cancellationToken);
    }

    public async Task<byte[]> ReadWorkspaceBytesAsync(ToolExecutionContext context, string path, int maximumBytes, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateOwner(context);
        if (!context.RequireOsIsolation || !_workers.TryGetValue(context.SessionId!, out var worker) ||
            !SamePath(worker.Workspace, context.WorkspacePath) || worker.ActiveRunId != context.RunId || worker.ExecutionMode != context.ExecutionMode)
            throw new InvalidOperationException("Workspace read does not belong to an active isolated chat/run policy.");
        return await ReadFromWorkerAsync(worker, context.RunId!, path, maximumBytes, cancellationToken);
    }

    private static async Task<byte[]> ReadFromWorkerAsync(Worker worker, string runId, string path, int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is < 1 or > 4 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        worker.ThrowIfLost();
        IsolationReadBytesResult result;
        try
        {
            result = await worker.Rpc.InvokeWithParameterObjectAsync<IsolationReadBytesResult>(IsolationMethods.ReadBytes,
                new IsolationReadBytesRequest(path, maximumBytes, runId), cancellationToken: cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch (RemoteInvocationException ex) { throw new IOException("The workspace file could not be safely read inside the required OS boundary.", ex); }
        catch (ConnectionLostException ex)
        {
            await worker.DisposeAsync();
            throw new IOException("The isolated workspace reader was lost; no broker-side file read was attempted.", ex);
        }
        catch (OperationCanceledException) { await worker.DisposeAsync(); throw; }
        catch (TimeoutException ex)
        {
            await worker.DisposeAsync();
            throw new IOException("The isolated workspace reader exceeded its local deadline; no read was replayed.", ex);
        }
        if (result.Missing) throw new FileNotFoundException("Workspace file does not exist.", path);
        if (result.Content is null || result.Content.Length > maximumBytes) throw new InvalidDataException("Worker read exceeded its byte budget.");
        return result.Content;
    }

    public async Task<int> EndRunAsync(string sessionId, string runId)
    {
        if (!_workers.TryGetValue(sessionId, out var worker)) return 0;
        await worker.EndRunGate.WaitAsync();
        try
        {
            // Lease disposal and the kernel resource-lifetime hook both observe cleanup.
            // Preserve its original unfinished count rather than converting it to zero on replay.
            if (worker.LastEndedRun == runId) return worker.LastEndResult;
            int count;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                worker.ThrowIfLost();
                count = await worker.Rpc.InvokeWithParameterObjectAsync<int>(IsolationMethods.EndRun,
                    new IsolationEndRunRequest(runId), cancellationToken: deadline.Token).WaitAsync(deadline.Token);
            }
            catch (Exception ex) when (ex is ConnectionLostException or RemoteInvocationException or IOException or OperationCanceledException or ObjectDisposedException)
            { await worker.DisposeAsync(); count = 1; }
            worker.LastEndedRun = runId;
            worker.LastEndResult = count;
            if (worker.ActiveRunId == runId) worker.ActiveRunId = null;
            return count;
        }
        finally { worker.EndRunGate.Release(); }
    }

    public async Task<int> EndSessionAsync(string sessionId)
    {
        if (!_workers.TryRemove(sessionId, out var worker)) return 0;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));
            await worker.Rpc.InvokeWithParameterObjectAsync<int>(IsolationMethods.EndSession, argument: (object?)null, cancellationToken: deadline.Token).WaitAsync(deadline.Token);
        }
        catch (Exception ex) when (ex is ConnectionLostException or RemoteInvocationException or IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { await worker.DisposeAsync(); }
        return 1;
    }

    public async ValueTask DisposeAsync()
    {
        await _starts.WaitAsync();
        try { if (_disposed) return; _disposed = true; }
        finally { _starts.Release(); }
        await Task.WhenAll(_workers.Keys.Select(EndSessionAsync));
    }

    private sealed record ConfigurationSnapshot(Dictionary<string, byte[]> Files, string Digest);
    private async Task<ConfigurationSnapshot> CaptureConfigurationAsync(CancellationToken ct)
    {
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in new[] { "lsp.json", "mcp.json" })
        {
            var source = Path.Combine(_protectedDataDirectory, name);
            if (!File.Exists(source)) continue;
            await using var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var bytes = new byte[65_537];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, ct);
            if (count > 65_536) throw new InvalidDataException("Isolated integration configuration exceeds 64 KiB.");
            using var parsed = JsonDocument.Parse(bytes.AsMemory(0, count));
            files.Add(name, bytes.AsSpan(0, count).ToArray());
        }
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, bytes) in files) { hash.AppendData(Encoding.UTF8.GetBytes(name)); hash.AppendData(SHA256.HashData(bytes)); }
        return new(files, Convert.ToHexString(hash.GetHashAndReset()));
    }

    private static void ValidateOwner(ToolExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.SessionId) || string.IsNullOrWhiteSpace(context.RunId))
            throw new ArgumentException("Isolated tools require explicit chat and run ownership.");
    }
    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class ProxyTool(OsIsolationManager owner, ToolDefinition definition, IWorkspacePathTool? original) : IAgentTool, IWorkspacePathTool
    {
        public ToolDefinition Definition => definition;
        public IReadOnlyList<string> GetPaths(ToolCall call, string workspace) => original?.GetPaths(call, workspace) ?? [];
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default) =>
            owner.ExecuteAsync(this, call, context, cancellationToken);
    }
    private sealed class RunLease : IAgentToolSet
    {
        private readonly OsIsolationManager _owner;
        private readonly string _session;
        private readonly string _run;
        private readonly CancellationTokenRegistration _cancellation;
        public IReadOnlyList<IAgentTool> Tools { get; }
        private int _disposed;
        public RunLease(OsIsolationManager owner, Worker worker, string session, string run, IReadOnlyList<IAgentTool> tools, CancellationToken runCancellation)
        {
            _owner = owner; _session = session; _run = run; Tools = tools;
            // A completed start_command RPC no longer has a request cancellation channel.
            // Keep the parent run token bound to native ownership until ordinary lease disposal.
            _cancellation = runCancellation.UnsafeRegister(static state => _ = ((Worker)state!).DisposeAsync().AsTask(), worker);
        }
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cancellation.Dispose();
            await _owner.EndRunAsync(_session, _run);
        }
    }

    private sealed class Worker : IAsyncDisposable
    {
        public JsonRpc Rpc { get; }
        public string Workspace { get; }
        public string Scratch { get; }
        public ExecutionMode ExecutionMode { get; }
        public string ConfigurationDigest { get; }
        public string? ActiveRunId;
        public SemaphoreSlim EndRunGate { get; } = new(1, 1);
        public string? LastEndedRun;
        public int LastEndResult;
        private readonly MxcProcess _process;
        private readonly WorkerStartupDiagnostics _diagnostics;
        private readonly Task _stderr;
        private readonly object _disposeGate = new();
        private Task? _disposeTask;
        private string _exitStatus = "unobserved";
        private int _disposed;
        public Worker(MxcProcess process, string workspace, string scratch, ExecutionMode mode, string configurationDigest)
        {
            _process = process; Workspace = workspace; Scratch = scratch; ExecutionMode = mode; ConfigurationDigest = configurationDigest;
            if (process.StandardInput is null || process.StandardOutput is null || process.StandardError is null)
                throw new InvalidOperationException("Native tool worker requires owned standard streams.");
            _diagnostics = new([workspace, scratch, AppContext.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Path.GetTempPath(),
                Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."))]);
            _stderr = _diagnostics.DrainAsync(process.StandardError);
            Rpc = new JsonRpc(BoundedRpcPipes.CreateHandler(process.StandardInput, process.StandardOutput, new SystemTextJsonFormatter()));
            Rpc.StartListening();
            _ = Rpc.Completion.ContinueWith(static (completed, state) => { _ = ((Worker)state!).DisposeAsync().AsTask(); },
                this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        public void ThrowIfLost()
        {
            if (Volatile.Read(ref _disposed) != 0 || Rpc.Completion.IsCompleted || _process.TryGetExitCode(out _))
                throw new IOException("The owned isolated worker was lost. Prior outcomes remain unknown; explicitly stop the chat before starting a fresh worker. " + DiagnosticSummary());
        }
        public string DiagnosticSummary() => "Native exit=" + _exitStatus + ". " + _diagnostics.Summary();
        public ValueTask DisposeAsync()
        {
            lock (_disposeGate)
            {
                if (_disposeTask is null)
                {
                    Interlocked.Exchange(ref _disposed, 1);
                    _disposeTask = Task.Run(DisposeCoreAsync);
                }
                return new(_disposeTask);
            }
        }
        private async Task DisposeCoreAsync()
        {
            try { if (_process.TryGetExitCode(out var code)) _exitStatus = code.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is MxcException or ObjectDisposedException) { }
            // Preserve startup stderr that may still be draining after native stdout EOF.
            try { await _stderr.WaitAsync(TimeSpan.FromMilliseconds(250)); }
            catch (TimeoutException) { }
            // Kill the native sandbox, not merely its root PID. Native ownership controls descendants.
            try { _process.Kill(); }
            finally
            {
                Rpc.Dispose();
                _process.Dispose();
                try { await _stderr.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException) { }
            }
            // Scratch contains untrusted symlinks after command execution. Do not recursively
            // traverse it in the privileged broker; leave cleanup to explicit temp maintenance.
        }
    }
}
