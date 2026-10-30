using L = Workbench.Desktop.Localization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Workbench.Desktop.Remote;
using Workbench.Protocol;

namespace Workbench.Desktop;

/// <summary>A private child process with a single continuous protocol reader.</summary>
internal sealed class HostClient : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<HostConnectionProfile, ProcessStartInfo>? _startFactory;
    private readonly object _diagnosticLock = new();
    private readonly HashSet<string> _diagnosticHints = [];
    private Process? _process;
    private Task? _reader;
    private Task? _diagnostics;
    private long _nextId;
    private bool _disposed;

    public event Action<string, JsonElement>? Notification;
    public event Action<string>? Disconnected;
    public bool Connected => !_disposed && _process is { HasExited: false };
    public HostConnectionProfile Connection { get; private set; } = HostConnectionProfile.Local;
    public bool IsRemote => Connection.IsRemote;
    public string DisplayLabel => Connection.DisplayLabel;
    public IReadOnlyList<string> SecretEnvironmentVariables { get; set; } = [];

    internal HostClient(Func<HostConnectionProfile, ProcessStartInfo>? startFactory = null) => _startFactory = startFactory;

    public Task StartAsync(CancellationToken cancellationToken = default) => StartAsync(HostConnectionProfile.Local, cancellationToken);

    public async Task StartAsync(HostConnectionProfile connection, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        connection.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (Connected)
        {
            if (Connection == connection) return;
            throw new InvalidOperationException("Create a new Host client to switch connections.");
        }
        if (_process is not null) throw new InvalidOperationException("Create a new Host client to reconnect after a disconnect.");
        if (_startFactory is null && connection.Kind == HostConnectionKind.Wsl && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WSL connections are available on Windows only.");
        Connection = connection;
        try
        {
            var start = _startFactory?.Invoke(connection) ?? (connection.IsRemote ? RemoteHostLaunch.CreateStartInfo(connection) : CreateLocalStartInfo());
            if (connection.IsRemote) RemoteHostLaunch.SanitizeEnvironment(start, SecretEnvironmentVariables);
            _process = Process.Start(start) ?? throw new InvalidOperationException(L.Text("HostStartFailed"));
            _diagnostics = DrainDiagnosticsAsync(_process.StandardError, _lifetime.Token);
            _reader = ReadLoopAsync(_process.StandardOutput, _lifetime.Token);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(20));
            var initialized = await RequestAsync<JsonElement>("initialize", new { protocolVersion = Wire.Version }, startup.Token).ConfigureAwait(false);
            if (initialized.ValueKind != JsonValueKind.Object || !initialized.TryGetProperty("protocolVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var protocolVersion) || protocolVersion != Wire.Version)
                throw new InvalidDataException($"The Host protocol does not match desktop version {Wire.Version}. Install the matching Cogitide Host on this endpoint.");
            if (connection.IsRemote && (!initialized.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array ||
                !RequiredRemoteCapabilities.All(required => capabilities.EnumerateArray().Any(capability => capability.ValueKind == JsonValueKind.String && capability.GetString() == required))))
                throw new InvalidDataException("The remote Host is missing required capabilities (session.view, run.continue). Install the matching Cogitide Host on this endpoint.");
        }
        catch (Exception exception)
        {
            await DisposeAsync().ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            if (!connection.IsRemote) throw;
            var reason = exception switch
            {
                InvalidDataException => exception.Message,
                JsonException => "The remote process did not return a valid Host protocol response.",
                OperationCanceledException => "The remote Host did not initialize within 20 seconds.",
                System.ComponentModel.Win32Exception => "The system SSH or WSL executable could not be started. Verify that it is installed.",
                _ => "The remote Host stopped before initialization completed. Verify the preinstalled Host path and its runtime."
            };
            string[] hints;
            lock (_diagnosticLock) hints = _diagnosticHints.ToArray();
            throw new IOException($"Cannot connect to {connection.DisplayLabel}. {reason}" + (hints.Length == 0 ? "" : " " + string.Join(' ', hints)), exception);
        }
    }

    private static ProcessStartInfo CreateLocalStartInfo()
    {
        var (hostPath, dotnetPath) = LocateHost();
        var start = new ProcessStartInfo
        {
            FileName = hostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? dotnetPath : hostPath,
            WorkingDirectory = Path.GetDirectoryName(hostPath)!,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        if (hostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(hostPath);
        start.ArgumentList.Add("--stdio");
        if (Program.DataDirectory is { } dataDirectory)
        {
            start.ArgumentList.Add("--data-dir");
            start.ArgumentList.Add(dataDirectory);
        }
        if (Path.IsPathFullyQualified(dotnetPath)) start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnetPath)!;
        return start;
    }

    public async Task<T> RequestAsync<T>(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!Connected) throw new InvalidOperationException(L.Format("HostDisconnected", ProductIdentity.DisplayName));
        var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException(L.Text("DuplicateRequest"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        timeout.CancelAfter(method == "workspace.command" ? TimeSpan.FromMinutes(12) : TimeSpan.FromSeconds(45));
        using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
        try
        {
            var request = new RpcRequest(id, method, Wire.Element(parameters ?? new { }));
            var json = JsonSerializer.Serialize(request, Wire.Json);
            await _writeLock.WaitAsync(timeout.Token).ConfigureAwait(false);
            try
            {
                await _process!.StandardInput.WriteLineAsync(json.AsMemory(), timeout.Token).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
            var result = await completion.Task.ConfigureAwait(false);
            if (typeof(T) == typeof(JsonElement)) return (T)(object)result;
            return result.Deserialize<T>(Wire.Json) ?? throw new InvalidDataException(L.Format("HostEmptyResponse", method));
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task ReadLoopAsync(StreamReader output, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            while (await output.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null)
                {
                    var id = idElement.ValueKind == JsonValueKind.String ? idElement.GetString()! : idElement.GetRawText();
                    if (!_pending.TryGetValue(id, out var completion)) continue;
                    if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
                    {
                        var message = error.ValueKind == JsonValueKind.String ? error.GetString() : error.GetRawText();
                        completion.TrySetException(new InvalidOperationException(message ?? L.Text("HostRequestFailed")));
                    }
                    else completion.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : Wire.Element(new { }));
                }
                else if (root.TryGetProperty("method", out var method) && root.TryGetProperty("params", out var parameters))
                {
                    Notification?.Invoke(method.GetString() ?? "", parameters.Clone());
                }
            }
            failure = new IOException(L.Format("HostStopped", ProductIdentity.DisplayName));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { failure = ex; }
        finally
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(failure ?? new OperationCanceledException(L.Text("HostClosed")));
            if (!_disposed && failure is not null) Disconnected?.Invoke(failure.Message);
        }
    }

    private async Task DrainDiagnosticsAsync(StreamReader diagnostics, CancellationToken cancellationToken)
    {
        // Keep draining even after the bounded classification window. Only fixed, redacted categories
        // are retained: arbitrary stderr can contain credentials and is never shown or persisted.
        var buffer = new char[1024];
        var tail = "";
        var remaining = 16_384;
        try
        {
            while (await diagnostics.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) is var count && count > 0)
            {
                if (remaining <= 0) continue;
                var sample = tail + new string(buffer, 0, Math.Min(count, remaining));
                remaining -= count;
                foreach (var (pattern, hint) in DiagnosticCategories)
                    if (sample.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        lock (_diagnosticLock) _diagnosticHints.Add(hint);
                tail = sample.Length > 128 ? sample[^128..] : sample;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static readonly (string Pattern, string Hint)[] DiagnosticCategories =
    [
        ("Permission denied", "SSH authentication failed; configure a key or agent for this destination. Interactive password prompts are disabled."),
        ("Host key verification failed", "SSH host-key verification failed. Verify the fingerprint through a trusted channel and configure known_hosts outside Cogitide."),
        ("REMOTE HOST IDENTIFICATION HAS CHANGED", "The SSH host key changed. Verify it through a trusted channel before changing known_hosts."),
        ("Could not resolve hostname", "SSH could not resolve the hostname or configuration alias."),
        ("Connection refused", "The SSH endpoint refused the connection."),
        ("Connection timed out", "The SSH endpoint timed out."),
        ("not found", "The remote Host, dotnet runtime or WSL distribution was not found; deployment is manual."),
        ("No such file", "The remote Host or runtime path does not exist; deployment is manual."),
        ("WSL_E_DISTRO_NOT_FOUND", "The selected WSL distribution is not installed.")
    ];

    private static readonly string[] RequiredRemoteCapabilities = ["session.view", "run.continue"];

    private static (string HostPath, string DotnetPath) LocateHost()
    {
        var explicitPath = Environment.GetEnvironmentVariable("WORKBENCH_HOST_PATH");
        var executableName = OperatingSystem.IsWindows() ? "Workbench.Host.exe" : "Workbench.Host";
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(Path.GetFullPath(explicitPath));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, executableName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "Workbench.Host.dll"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "host", executableName));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "host", "Workbench.Host.dll"));
        string? repository = null;
        foreach (var origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(origin); directory is not null; directory = directory.Parent)
            {
                if (!File.Exists(Path.Combine(directory.FullName, "global.json")) || !Directory.Exists(Path.Combine(directory.FullName, "src", "Workbench.Host"))) continue;
                repository = directory.FullName;
                foreach (var configuration in new[] { "Debug", "Release" })
                    candidates.Add(Path.Combine(repository, "src", "Workbench.Host", "bin", configuration, "net11.0", "Workbench.Host.dll"));
                break;
            }
        }
        var host = candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(L.Text("HostMissing"));
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var privateDotnet = repository is null ? null : Path.Combine(repository, ".tools", "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        if (string.IsNullOrWhiteSpace(dotnet) || !File.Exists(dotnet)) dotnet = privateDotnet is not null && File.Exists(privateDotnet) ? privateDotnet : "dotnet";
        return (host, dotnet);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_process is { } process)
        {
            try
            {
                process.StandardInput.Close();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { ProcessShutdown.Terminate(process); }
            catch (Exception ex) when (ProcessShutdown.IsShutdownRace(ex)) { ProcessShutdown.Terminate(process); }
        }
        _lifetime.Cancel();
        try { if (_reader is not null) await _reader.ConfigureAwait(false); } catch (OperationCanceledException) { }
        try { if (_diagnostics is not null) await _diagnostics.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _process?.Dispose();
        _process = null;
        _lifetime.Dispose();
        _writeLock.Dispose();
    }
}
