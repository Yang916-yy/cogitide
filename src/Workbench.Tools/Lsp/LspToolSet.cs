using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workbench.Protocol;

namespace Workbench.Tools;

/// <summary>Lazy, explicitly configured, run-owned language servers. No automatic server installation.</summary>
public sealed partial class LspToolSet : IAgentToolSet
{
    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal)
        { "typescript", "typescriptreact", "javascript", "javascriptreact", "python", "csharp", "go", "rust" };
    private static readonly Dictionary<string, string> LanguageByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ts"] = "typescript", [".mts"] = "typescript", [".cts"] = "typescript", [".tsx"] = "typescriptreact", [".js"] = "javascript", [".jsx"] = "javascriptreact",
        [".mjs"] = "javascript", [".cjs"] = "javascript", [".py"] = "python", [".pyi"] = "python",
        [".cs"] = "csharp", [".go"] = "go", [".rs"] = "rust"
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string _workspace;
    private readonly LspWorkspaceConfiguration _configuration;
    private readonly HashSet<string> _excluded;
    private readonly Dictionary<string, LspClient> _clients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationTokenRegistration _lifetime;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    public IReadOnlyList<IAgentTool> Tools { get; }

    public LspToolSet(string workspace, LspWorkspaceConfiguration configuration,
        IReadOnlyList<string>? excludedEnvNames = null, CancellationToken lifetime = default)
    {
        _workspace = WorkspaceService.Root(workspace);
        // Detach mutable arrays and JsonElement lifetimes from caller-owned configuration objects.
        _configuration = JsonSerializer.Deserialize<LspWorkspaceConfiguration>(JsonSerializer.Serialize(configuration, ConfigJson), ConfigJson)!;
        Validate(_configuration, _workspace);
        _excluded = new(excludedEnvNames ?? [], StringComparer.OrdinalIgnoreCase);
        _excluded.UnionWith(["OPENAI_API_KEY", "ANTHROPIC_API_KEY", "DEEPSEEK_API_KEY", "AZURE_OPENAI_API_KEY", "CODEX_API_KEY", "COGITIDE_ACCOUNT_TOKEN"]);
        Tools = _configuration.Servers.Length == 0 ? [] : [new QueryTool(this)];
        _lifetime = lifetime.UnsafeRegister(static state => _ = ((LspToolSet)state!).DisposeAsync().AsTask(), this);
    }

    /// <summary>Positions are one-based UTF-16; returned ranges use the same convention and exclusive ends.</summary>
    public async Task<ToolResult> QueryAsync(string action, string path, int line = 1, int column = 1,
        string? serverId = null, int offset = 0, int limit = 100, string? expectedSnapshot = null,
        CancellationToken ct = default)
    {
        if (action is not ("definition" or "references" or "hover" or "diagnostics" or "symbols"))
            throw new ArgumentException("action must be definition, references, hover, diagnostics or symbols.");
        if (line < 1 || column < 1 || offset is < 0 or > 10000 || limit is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(line), "Invalid one-based position or result page.");
        var full = WorkspaceService.Resolve(_workspace, path);
        path = Relative(full);
        if (!LanguageByExtension.TryGetValue(Path.GetExtension(path), out var language))
            throw new ArgumentException("No supported LSP language for this file extension.");
        var matches = _configuration.Servers.Where(s => (serverId is null || s.Id == serverId) &&
            s.Languages.Contains(language, StringComparer.Ordinal) && Within(full, WorkspaceService.Resolve(_workspace, s.Root, true))).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException(matches.Length == 0
            ? "No explicitly configured LSP server matches this file. Configure an installed server and trusted root."
            : "Multiple LSP servers match this file; choose serverId explicitly.");
        var configuration = matches[0];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(configuration.RequestTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, _shutdown.Token);
        try { await _gate.WaitAsync(linked.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        { return new("LSP request exceeded its time budget while waiting for the owned server. No request was sent.", true); }
        try
        {
            ObjectDisposedException.ThrowIf(_shutdown.IsCancellationRequested, this);
            var snapshot = await CaptureAsync(configuration, linked.Token);
            var source = snapshot.Sources.SingleOrDefault(s => string.Equals(s.Path, path,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                ?? throw new UnauthorizedAccessException("Requested source is ignored or outside the bounded LSP source inventory.");
            path = source.Path;
            LspPositions.FromUtf16(LineAt(source.Text, line - 1), column - 1, "utf-16");
            if (!_clients.TryGetValue(configuration.Id, out var client))
            {
                client = await LspClient.StartAsync(configuration, _workspace, _excluded, linked.Token);
                _clients.Add(configuration.Id, client);
            }
            await client.SynchronizeAsync(snapshot.Sources, snapshot.ConfigurationHashes, linked.Token);
            var records = await client.QueryAsync(action, source, line, column, linked.Token);
            var projected = Project(action, records.Result, path, snapshot.Sources, client.PositionEncoding, out var dropped, out var projectionTruncated);
            // Do not return evidence from a source edit racing server analysis.
            var after = await CaptureAsync(configuration, linked.Token);
            if (after.Digest != snapshot.Digest) return new("LSP source changed during analysis. No result page was committed; retry from offset 0.", true);
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                { action, path, line, column, serverId = configuration.Id, snapshot.Digest, projected, records.DiagnosticsObserved, records.DiagnosticVersionVerified }, Wire.Json))));
            if (expectedSnapshot is not null && !string.Equals(identity, expectedSnapshot, StringComparison.OrdinalIgnoreCase))
                return new("LSP source or result snapshot changed. Restart the query from offset 0.", true);
            var items = new JsonArray(projected.Skip(offset).Take(limit).Select(n => n.DeepClone()).ToArray());
            var response = new JsonObject
            {
                ["server"] = configuration.Id, ["action"] = action, ["path"] = path, ["snapshot"] = identity,
                ["contentTrust"] = "untrusted language-server output",
                ["sha256"] = source.Sha256, ["positionEncoding"] = "utf-16", ["lineBase"] = 1,
                ["results"] = items, ["offset"] = offset, ["total"] = projected.Count,
                ["nextOffset"] = offset + items.Count < projected.Count ? offset + items.Count : null,
                ["truncated"] = offset + items.Count < projected.Count || dropped > 0 || projectionTruncated,
                ["projectionTruncated"] = projectionTruncated,
                ["omittedExternalOrInvalidLocations"] = dropped, ["diagnosticsObserved"] = records.DiagnosticsObserved,
                ["diagnosticVersionVerified"] = records.DiagnosticVersionVerified,
                ["scope"] = new JsonObject { ["root"] = configuration.Root, ["files"] = snapshot.Sources.Length,
                    ["inventorySource"] = snapshot.InventorySource, ["completeProject"] = false,
                    ["snapshotScope"] = "bounded source and project-configuration files; server dependencies and external state are not snapshotted" }
            };
            var serialized = response.ToJsonString(Wire.Json);
            while (serialized.Length > 60_000 && items.Count > 0)
            {
                items.RemoveAt(items.Count - 1);
                response["truncated"] = true;
                response["nextOffset"] = offset + items.Count;
                serialized = response.ToJsonString(Wire.Json);
            }
            if (items.Count == 0 && projected.Count > offset)
                return new("LSP result record exceeds the output budget; narrow the query.", true);
            return new(serialized);
        }
        catch (OperationCanceledException)
        {
            // Full sync and server analysis may already have started. Never reuse ambiguous state or replay a request.
            if (_clients.Remove(configuration.Id, out var client)) await client.DisposeAsync();
            if (ct.IsCancellationRequested || _shutdown.IsCancellationRequested) throw;
            return new("LSP request exceeded its time budget. Owned server stopped; no result committed and no request replayed.", true);
        }
        catch (Exception ex) when (ex is StreamJsonRpc.ConnectionLostException or StreamJsonRpc.RemoteInvocationException or IOException)
        {
            if (_clients.Remove(configuration.Id, out var client)) await client.DisposeAsync();
            return new($"LSP request failed ({ex.GetType().Name}). Owned server stopped; no request replayed.", true);
        }
        finally { _gate.Release(); }
    }

    internal sealed record Source(string Path, string Uri, string Language, string Text, string Sha256);
    private sealed record SourceSnapshot(Source[] Sources, string Digest, string InventorySource, Dictionary<string, string> ConfigurationHashes);
    private async Task<SourceSnapshot> CaptureAsync(LspServerConfiguration configuration, CancellationToken ct)
    {
        var root = WorkspaceService.Resolve(_workspace, configuration.Root, true);
        var prefix = root == _workspace ? "" : Relative(root) + "/";
        var inventory = await new WorkspaceService().InventoryAsync(_workspace, prefix, ct);
        if (inventory.Truncated) throw new IOException("LSP inventory is truncated; configure a smaller server root.");
        var relevant = inventory.Files.Where(p => LanguageByExtension.TryGetValue(Path.GetExtension(p), out var l) &&
            configuration.Languages.Contains(l, StringComparer.Ordinal) || IsConfiguration(p)).Order(StringComparer.Ordinal).ToArray();
        if (relevant.Length > 512) throw new IOException("LSP snapshot exceeds 512 source/configuration files; configure a smaller root.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(configuration, ConfigJson));
        List<Source> sources = [];
        Dictionary<string, string> configurationHashes = new(StringComparer.Ordinal);
        var totalBytes = 0;
        var bytes = ArrayPool<byte>.Shared.Rent(1024 * 1024 + 1);
        try
        {
            foreach (var path in relevant)
            {
                var full = WorkspaceService.Resolve(_workspace, path);
                await using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (file.Length > 1024 * 1024) throw new IOException("LSP snapshot file exceeds 1 MiB; configure a smaller root.");
                var count = await file.ReadAtLeastAsync(bytes.AsMemory(0, 1024 * 1024 + 1), 1024 * 1024 + 1, false, ct);
                if (count > 1024 * 1024 || (totalBytes += count) > 16 * 1024 * 1024)
                    throw new IOException("LSP snapshot exceeds its 1 MiB per file / 16 MiB total budget.");
                var digest = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0, count)));
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { path, digest }, Wire.Json));
                if (IsConfiguration(path)) configurationHashes[new Uri(full).AbsoluteUri] = digest;
                if (LanguageByExtension.TryGetValue(Path.GetExtension(path), out var language) && configuration.Languages.Contains(language, StringComparer.Ordinal))
                {
                    var text = StrictUtf8.GetString(bytes, 0, count);
                    if (text.StartsWith('\uFEFF')) text = text[1..];
                    if (text.Contains('\0')) throw new IOException("LSP sources must be UTF-8 text without NUL characters.");
                    sources.Add(new(path, new Uri(full).AbsoluteUri, language, text, digest));
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(bytes, clearArray: true); }
        return new(sources.ToArray(), Convert.ToHexString(hash.GetHashAndReset()), inventory.Source, configurationHashes);
    }
    private static bool IsConfiguration(string path) => Path.GetExtension(path).ToLowerInvariant() is
        ".json" or ".toml" or ".yaml" or ".yml" or ".csproj" or ".props" or ".targets" or ".sln" or ".slnx" ||
        Path.GetFileName(path) is "go.mod" or "go.sum" or "rust-toolchain" or "Cargo.lock" or ".editorconfig";
    private string Relative(string full) => Path.GetRelativePath(_workspace, full).Replace(Path.DirectorySeparatorChar, '/');
    private static bool Within(string file, string root) => file.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new(_disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        await _shutdown.CancelAsync();
        await _gate.WaitAsync();
        try
        {
            List<Exception> errors = [];
            foreach (var client in _clients.Values)
                try { await client.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
            _clients.Clear();
            if (errors.Count > 0) throw new AggregateException("LSP cleanup was not fully verified.", errors);
        }
        finally { _gate.Release(); _lifetime.Unregister(); }
    }

    private sealed class QueryTool(LspToolSet owner) : IAgentTool, IWorkspacePathTool
    {
        public ToolDefinition Definition { get; } = new("lsp_query",
            "Query an explicitly configured language server for definition, references, hover, diagnostics or symbols. Requires approval because a server runs with user privileges and may evaluate project configuration. No auto-install. Paths are workspace-relative; positions are one-based UTF-16. Use expectedSnapshot on later pages; inspect truncation and diagnosticsObserved.",
            """{"type":"object","properties":{"action":{"type":"string","enum":["definition","references","hover","diagnostics","symbols"]},"path":{"type":"string"},"line":{"type":"integer","minimum":1},"column":{"type":"integer","minimum":1},"serverId":{"type":"string"},"offset":{"type":"integer","minimum":0,"maximum":10000},"limit":{"type":"integer","minimum":1,"maximum":500},"expectedSnapshot":{"type":"string"}},"required":["action","path"],"additionalProperties":false}""", RequiresApproval: true);
        public IReadOnlyList<string> GetPaths(ToolCall call, string workspace)
        {
            using var args = JsonDocument.Parse(call.ArgumentsJson);
            return [WorkspaceService.Resolve(workspace, args.RootElement.GetProperty("path").GetString()!)];
        }
        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                if (!string.Equals(WorkspaceService.Root(context.WorkspacePath), owner._workspace,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("LSP tool belongs to another workspace.");
                using var document = JsonDocument.Parse(call.ArgumentsJson);
                var p = document.RootElement;
                string Required(string name) => p.GetProperty(name).GetString() ?? throw new ArgumentException($"Missing {name}.");
                string? Optional(string name) => p.TryGetProperty(name, out var v) ? v.GetString() : null;
                int Number(string name, int fallback) => p.TryGetProperty(name, out var v) ? v.GetInt32() : fallback;
                return await owner.QueryAsync(Required("action"), Required("path"), Number("line", 1), Number("column", 1),
                    Optional("serverId"), Number("offset", 0), Number("limit", 100), Optional("expectedSnapshot"), cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or InvalidOperationException or JsonException or KeyNotFoundException or System.ComponentModel.Win32Exception)
            { return new(ex.Message, true); }
        }
    }
}
