using System.Diagnostics;
using System.Text.Json;
using StreamJsonRpc;

namespace Workbench.Tools;

/// <summary>Small LSP adapter; Microsoft StreamJsonRpc owns framing, JSON-RPC, dispatch and cancellation.</summary>
internal sealed class LspClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly JsonRpc _rpc;
    private readonly LspServerConfiguration _configuration;
    private readonly string _rootUri;
    private readonly Task _stderr;
    private readonly Dictionary<string, OpenDocument> _open = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private Dictionary<string, string> _configurationHashes = new(StringComparer.Ordinal);
    private readonly object _diagnosticLock = new();
    private bool _pullDiagnostics;
    private JsonElement _capabilities;
    private bool _disposed;
    public string PositionEncoding { get; private set; } = "utf-16";
    private sealed class OpenDocument(LspToolSet.Source source)
    {
        public LspToolSet.Source Source = source;
        public int Version = 1;
        public JsonElement? Diagnostics;
        public bool VersionVerified;
        public TaskCompletionSource<bool> Published = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private LspClient(Process process, LspServerConfiguration configuration, string root)
    {
        _process = process;
        _configuration = configuration;
        _rootUri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
        _stderr = DrainAsync(process.StandardError);
        var formatter = new SystemTextJsonFormatter();
        _rpc = new JsonRpc(BoundedRpcPipes.CreateHandler(process.StandardInput.BaseStream,
            process.StandardOutput.BaseStream, formatter), new Callbacks(this));
        _rpc.StartListening();
    }
    public static async Task<LspClient> StartAsync(LspServerConfiguration configuration, string workspace,
        HashSet<string> excluded, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = WorkspaceService.Resolve(workspace, configuration.Root, true);
        var start = new ProcessStartInfo(configuration.Command)
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in configuration.Arguments) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "LANG", "LC_ALL", "DOTNET_ROOT", "ProgramFiles", "ProgramFiles(x86)" }
            .Concat(configuration.EnvironmentVariables ?? []).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!excluded.Contains(name) && Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        var process = new Process { StartInfo = start };
        LspClient? client = null;
        try
        {
            if (!process.Start()) throw new IOException("Configured LSP process failed to start.");
            client = new(process, configuration, root);
            var initialized = await client._rpc.InvokeWithParameterObjectAsync<JsonElement>("initialize", new
            {
                processId = Environment.ProcessId,
                clientInfo = new { name = "Cogitide", version = "1.0" },
                rootUri = client._rootUri,
                workspaceFolders = new[] { new { uri = client._rootUri, name = Path.GetFileName(root) } },
                initializationOptions = configuration.InitializationOptions,
                capabilities = new
                {
                    general = new { positionEncodings = new[] { "utf-16", "utf-8", "utf-32" } },
                    workspace = new { configuration = true, workspaceFolders = true, applyEdit = false },
                    textDocument = new
                    {
                        synchronization = new { dynamicRegistration = false, didSave = false },
                        definition = new { dynamicRegistration = false, linkSupport = true },
                        references = new { dynamicRegistration = false },
                        hover = new { dynamicRegistration = false, contentFormat = new[] { "plaintext", "markdown" } },
                        documentSymbol = new { dynamicRegistration = false, hierarchicalDocumentSymbolSupport = true },
                        publishDiagnostics = new { versionSupport = true },
                        diagnostic = new { dynamicRegistration = false }
                    }
                }
            }, cancellationToken: ct).WaitAsync(ct);
            client._capabilities = initialized.GetProperty("capabilities").Clone();
            if (client._capabilities.TryGetProperty("positionEncoding", out var encoding))
                client.PositionEncoding = encoding.GetString() ?? "utf-16";
            if (client.PositionEncoding is not ("utf-16" or "utf-8" or "utf-32"))
                throw new InvalidDataException("LSP server selected an unsupported position encoding.");
            client._pullDiagnostics = client._capabilities.TryGetProperty("diagnosticProvider", out var diagnostic) && diagnostic.ValueKind is JsonValueKind.Object or JsonValueKind.True;
            await client._rpc.NotifyWithParameterObjectAsync("initialized", new { }).WaitAsync(ct);
            if (configuration.Settings is { } settings)
                await client._rpc.NotifyWithParameterObjectAsync("workspace/didChangeConfiguration", new { settings }).WaitAsync(ct);
            return client;
        }
        catch
        {
            if (client is not null) await client.DisposeAsync();
            else { try { if (process.Id > 0) await WorkspaceService.StopProcessAsync(process); } catch (InvalidOperationException) { } finally { process.Dispose(); } }
            throw;
        }
    }
    public async Task SynchronizeAsync(LspToolSet.Source[] sources, Dictionary<string, string> configurationHashes, CancellationToken ct)
    {
        var configurationChanges = configurationHashes.Where(pair => !_configurationHashes.TryGetValue(pair.Key, out var digest) || digest != pair.Value)
            .Select(pair => new { uri = pair.Key, type = _configurationHashes.ContainsKey(pair.Key) ? 2 : 1 })
            .Concat(_configurationHashes.Keys.Where(uri => !configurationHashes.ContainsKey(uri)).Select(uri => new { uri, type = 3 })).ToArray();
        if (configurationChanges.Length > 0)
            await _rpc.NotifyWithParameterObjectAsync("workspace/didChangeWatchedFiles", new { changes = configurationChanges }).WaitAsync(ct);
        _configurationHashes = configurationHashes;
        var present = sources.Select(s => s.Uri).ToHashSet(StringComparer.Ordinal);
        string[] deleted;
        lock (_diagnosticLock) deleted = _open.Keys.Where(uri => !present.Contains(uri)).ToArray();
        foreach (var uri in deleted)
        {
            await _rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new { textDocument = new { uri } }).WaitAsync(ct);
            lock (_diagnosticLock) _open.Remove(uri);
            await _rpc.NotifyWithParameterObjectAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri, type = 3 } } }).WaitAsync(ct);
        }
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            OpenDocument document;
            bool created;
            lock (_diagnosticLock)
            {
                created = !_open.TryGetValue(source.Uri, out document!);
                if (created) _open.Add(source.Uri, document = new(source));
                else if (source.Sha256 == document.Source.Sha256 && configurationChanges.Length == 0) continue;
                else
                {
                    document.Source = source;
                    document.Version++;
                    document.Diagnostics = null;
                    document.VersionVerified = false;
                    document.Published = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
            await _rpc.NotifyWithParameterObjectAsync("workspace/didChangeWatchedFiles", new { changes = new[] { new { uri = source.Uri, type = created ? 1 : 2 } } }).WaitAsync(ct);
            if (created)
                await _rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new
                { textDocument = new { uri = source.Uri, languageId = source.Language, version = document.Version, text = source.Text } }).WaitAsync(ct);
            else
                await _rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new
                { textDocument = new { uri = source.Uri, version = document.Version }, contentChanges = new[] { new { text = source.Text } } }).WaitAsync(ct);
        }
    }
    internal sealed record QueryResult(JsonElement Result, bool? DiagnosticsObserved = null, bool? DiagnosticVersionVerified = null);
    public async Task<QueryResult> QueryAsync(string action, LspToolSet.Source source, int line, int column, CancellationToken ct)
    {
        var position = new { line = line - 1, character = LspPositions.FromUtf16(LspToolSet.LineAt(source.Text, line - 1), column - 1, PositionEncoding) };
        var textDocument = new { uri = source.Uri };
        if (action == "diagnostics")
        {
            if (_pullDiagnostics)
            {
                var report = await _rpc.InvokeWithParameterObjectAsync<JsonElement>("textDocument/diagnostic", new { textDocument }, cancellationToken: ct).WaitAsync(ct);
                if (report.TryGetProperty("kind", out var kind) && kind.GetString() == "unchanged")
                    throw new InvalidDataException("LSP server returned unchanged diagnostics without an existing result ID.");
                return new(report.GetProperty("items").Clone(), true, true);
            }
            OpenDocument open;
            lock (_diagnosticLock) open = _open[source.Uri];
            try { await open.Published.Task.WaitAsync(TimeSpan.FromSeconds(5), ct); }
            catch (TimeoutException) { return new(JsonSerializer.SerializeToElement(Array.Empty<object>()), false, false); }
            lock (_diagnosticLock) return new(open.Diagnostics ?? JsonSerializer.SerializeToElement(Array.Empty<object>()), true, open.VersionVerified);
        }
        var (method, capability) = action switch
        {
            "definition" => ("textDocument/definition", "definitionProvider"),
            "references" => ("textDocument/references", "referencesProvider"),
            "hover" => ("textDocument/hover", "hoverProvider"),
            "symbols" => ("textDocument/documentSymbol", "documentSymbolProvider"),
            _ => throw new ArgumentException("Unsupported LSP action.")
        };
        if (!_capabilities.TryGetProperty(capability, out var provider) || provider.ValueKind is JsonValueKind.False or JsonValueKind.Null)
            throw new InvalidOperationException($"Configured LSP server does not advertise {capability}.");
        object arguments = action == "symbols" ? new { textDocument } : action == "references"
            ? new { textDocument, position, context = new { includeDeclaration = false } } : new { textDocument, position };
        // LSP cancellation is advisory: a server may ignore $/cancelRequest forever. The caller's local
        // deadline must independently release ownership so QueryAsync can close RPC and stop the process.
        return new(await _rpc.InvokeWithParameterObjectAsync<JsonElement>(method, arguments, cancellationToken: ct).WaitAsync(ct));
    }
    private sealed class Callbacks(LspClient owner)
    {
        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void PublishDiagnostics(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("uri", out var uri) || !parameters.TryGetProperty("diagnostics", out var diagnostics) || diagnostics.ValueKind != JsonValueKind.Array) return;
            string canonical;
            try { canonical = LspFileUris.Canonical(uri.GetString() ?? ""); }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return; }
            lock (owner._diagnosticLock)
            {
                if (!owner._open.TryGetValue(canonical, out var document)) return;
                var versioned = parameters.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number;
                if (versioned && version.GetInt32() != document.Version) return;
                document.Diagnostics = diagnostics.Clone();
                document.VersionVerified = versioned;
                document.Published.TrySetResult(true);
            }
        }
        [JsonRpcMethod("workspace/configuration", UseSingleObjectParameterDeserialization = true)]
        public object?[] Configuration(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 128) return [];
            return items.EnumerateArray().Select(item =>
            {
                JsonElement? selected = owner._configuration.Settings;
                if (item.TryGetProperty("section", out var section) && section.ValueKind == JsonValueKind.String)
                    foreach (var segment in (section.GetString() ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries))
                        selected = selected is { ValueKind: JsonValueKind.Object } current && current.TryGetProperty(segment, out var child) ? child : null;
                return (object?)selected;
            }).ToArray();
        }
        [JsonRpcMethod("workspace/workspaceFolders")]
        public object[] WorkspaceFolders() => [new { uri = owner._rootUri, name = "workspace" }];
        [JsonRpcMethod("workspace/applyEdit", UseSingleObjectParameterDeserialization = true)]
        public object ApplyEdit(JsonElement parameters) => new { applied = false, failureReason = "Language servers cannot bypass Cogitide editing tools and approval checks." };
        [JsonRpcMethod("window/workDoneProgress/create", UseSingleObjectParameterDeserialization = true)]
        public object? Progress(JsonElement parameters) => null;
        [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
        public void Log(JsonElement parameters) { }
        [JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)]
        public void Show(JsonElement parameters) { }
        [JsonRpcMethod("$/progress", UseSingleObjectParameterDeserialization = true)]
        public void ReportProgress(JsonElement parameters) { }
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer) > 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            try
            {
                await _rpc.InvokeWithParameterObjectAsync<JsonElement>("shutdown", argument: (object?)null, cancellationToken: deadline.Token).WaitAsync(deadline.Token);
                await _rpc.NotifyWithParameterObjectAsync("exit").WaitAsync(deadline.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or RemoteInvocationException or ConnectionLostException) { }
        }
        finally
        {
            try { _rpc.Dispose(); }
            finally
            {
                try { await WorkspaceService.StopProcessAsync(_process); }
                finally
                {
                    try { await _stderr.WaitAsync(TimeSpan.FromSeconds(2)); }
                    finally { _process.Dispose(); }
                }
            }
        }
    }
}
