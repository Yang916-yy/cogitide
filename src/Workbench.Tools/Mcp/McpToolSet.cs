using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Workbench.Protocol;

namespace Workbench.Tools;

/// <summary>One explicitly configured, run-owned set of official MCP SDK clients.</summary>
public sealed class McpToolSet : IAgentToolSet
{
    private const int ConfigBytes = 65_536;
    private const int MaximumTools = 64;
    private const int MaximumPages = 8;
    private const int MaximumSchemaCharacters = 32_768;
    private const int MaximumDefinitionCharacters = 262_144;
    private const int MaximumResultCharacters = 32_768;
    private readonly List<McpClient> _clients = [];
    private readonly List<IAsyncDisposable> _transports = [];
    private readonly List<HttpClient> _httpClients = [];
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private CancellationTokenRegistration _runCancellation;
    private readonly List<IAgentTool> _tools = [];
    public IReadOnlyList<IAgentTool> Tools => _tools.AsReadOnly();

    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        AllowDuplicateProperties = false
    };

    private sealed record Configuration(McpServerConfiguration[] Servers);
    private sealed record McpServerConfiguration(string Id, string Transport, string? Command = null,
        string[]? Arguments = null, string? Url = null, string[]? EnvironmentVariables = null,
        Dictionary<string, string>? HeaderEnvironmentVariables = null,
        int ConnectionTimeoutSeconds = 15, int CallTimeoutSeconds = 60);

    /// <summary>Loads a local opt-in configuration. An absent file means MCP is disabled.</summary>
    public static async Task<McpToolSet> ConnectAsync(string configPath, string workspace,
        IReadOnlyList<string>? excludedEnvNames = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var directory = Path.GetFullPath(workspace);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("MCP workspace does not exist.");
        var result = new McpToolSet();
        if (!File.Exists(configPath)) return result;
        Configuration config;
        await using (var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var bytes = new byte[ConfigBytes + 1];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, ct);
            if (count > ConfigBytes) throw new InvalidDataException("MCP configuration exceeds 64 KiB.");
            config = JsonSerializer.Deserialize<Configuration>(bytes.AsSpan(0, count), ConfigJson)
                ?? throw new InvalidDataException("MCP configuration must be an object.");
        }
        if (config.Servers is null || config.Servers.Length > 8)
            throw new InvalidDataException("MCP configuration requires a servers array with at most 8 entries.");
        var excluded = new HashSet<string>(excludedEnvNames ?? [], StringComparer.OrdinalIgnoreCase);
        excluded.UnionWith(["OPENAI_API_KEY", "ANTHROPIC_API_KEY", "DEEPSEEK_API_KEY", "AZURE_OPENAI_API_KEY",
            "OPENAI_ACCESS_TOKEN", "CODEX_ACCESS_TOKEN"]);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Validate every entry before starting any executable or making a network request.
        foreach (var server in config.Servers)
        {
            Validate(server, excluded);
            if (!ids.Add(server.Id)) throw new InvalidDataException("MCP server IDs must be unique.");
        }
        try
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var definitionCharacters = 0;
            foreach (var server in config.Servers)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(server.ConnectionTimeoutSeconds));
                try
                {
                    var transport = result.CreateTransport(server, directory, excluded);
                    var client = await McpClient.CreateAsync(transport, new McpClientOptions
                    {
                        ClientInfo = new Implementation { Name = "Cogitide", Version = "1.0" },
                        Capabilities = new ClientCapabilities(),
                        InitializationTimeout = TimeSpan.FromSeconds(server.ConnectionTimeoutSeconds),
                        DiscoverProbeTimeout = TimeSpan.FromSeconds(Math.Min(3, server.ConnectionTimeoutSeconds))
                    }, cancellationToken: deadline.Token);
                    result._clients.Add(client);
                    string? cursor = null;
                    var cursors = new HashSet<string>(StringComparer.Ordinal);
                    var remoteNames = new HashSet<string>(StringComparer.Ordinal);
                    for (var pageNumber = 0; ; pageNumber++)
                    {
                        if (pageNumber >= MaximumPages) throw new InvalidDataException("MCP tool discovery exceeds 8 pages.");
                        var page = await client.ListToolsAsync(new ListToolsRequestParams { Cursor = cursor }, deadline.Token);
                        foreach (var tool in page.Tools)
                        {
                            if (result._tools.Count >= MaximumTools) throw new InvalidDataException("MCP discovery exceeds 64 tools.");
                            if (string.IsNullOrWhiteSpace(tool.Name) || tool.Name.Length > 128 || !remoteNames.Add(tool.Name))
                                throw new InvalidDataException("MCP server returned an invalid or duplicate tool name.");
                            var schema = tool.InputSchema.GetRawText();
                            if (schema.Length > MaximumSchemaCharacters || tool.InputSchema.ValueKind != JsonValueKind.Object ||
                                !tool.InputSchema.TryGetProperty("type", out var type) || type.GetString() != "object")
                                throw new InvalidDataException("MCP tool input schema must be an object schema of at most 32768 characters.");
                            var name = LocalName(server.Id, tool.Name);
                            if (!names.Add(name)) throw new InvalidDataException("MCP tool namespace collision.");
                            var description = $"MCP server '{server.Id}', tool '{tool.Name}'. " + Clip(tool.Description ?? "", 2048);
                            definitionCharacters += schema.Length + description.Length + name.Length;
                            if (definitionCharacters > MaximumDefinitionCharacters)
                                throw new InvalidDataException("MCP tool definitions exceed the 256 KiB context limit.");
                            result._tools.Add(new RemoteTool(result, client, server, directory, tool.Name,
                                new ToolDefinition(name, description, schema, RequiresApproval: true)));
                        }
                        cursor = page.NextCursor;
                        if (cursor is null) break;
                        if (cursor.Length > 4096 || !cursors.Add(cursor))
                            throw new InvalidDataException("MCP server returned an invalid or repeated discovery cursor.");
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // SDK exceptions can include process stderr or HTTP headers. Do not log these implicitly.
                    throw new InvalidOperationException($"MCP server '{server.Id}' connection/discovery failed ({ex.GetType().Name}).");
                }
            }
            result._runCancellation = ct.UnsafeRegister(static state =>
                _ = ((McpToolSet)state!).DisposeAfterCancellationAsync(), result);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            try { await result.DisposeAsync(); } catch { /* Preserve the original connection failure. */ }
            throw;
        }
    }

    private IClientTransport CreateTransport(McpServerConfiguration server, string workspace, HashSet<string> excluded)
    {
        if (server.Transport == "stdio")
        {
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            foreach (var name in excluded) environment.Remove(name);
            foreach (var name in server.EnvironmentVariables ?? [])
                environment[name] = RequiredEnvironment(name);
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Id, Command = server.Command!, Arguments = server.Arguments ?? [],
                WorkingDirectory = workspace, InheritEnvironmentVariables = false,
                EnvironmentVariables = environment, ShutdownTimeout = TimeSpan.FromMilliseconds(500)
            });
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (header, variable) in server.HeaderEnvironmentVariables ?? [])
        {
            var value = RequiredEnvironment(variable);
            if (value.Length > 8192 || value.Contains('\r') || value.Contains('\n'))
                throw new InvalidDataException("MCP header environment value is invalid.");
            headers.Add(header, value);
        }
        // The SDK owns protocol framing, sessions and SSE; BCL owns HTTP, with credential redirects disabled.
        var httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(server.ConnectionTimeoutSeconds, server.CallTimeoutSeconds))
        };
        _httpClients.Add(httpClient);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = server.Id, Endpoint = new Uri(server.Url!), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers, ConnectionTimeout = TimeSpan.FromSeconds(server.ConnectionTimeoutSeconds),
            EnableStandaloneGetStream = false, MaxReconnectionAttempts = 0
        }, httpClient, ownsHttpClient: true);
        _transports.Add(transport);
        return transport;
    }

    private static void Validate(McpServerConfiguration? server, HashSet<string> excluded)
    {
        if (server is null || server.Id is null || !Regex.IsMatch(server.Id, "^[a-zA-Z0-9_-]{1,16}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("MCP server ID must contain 1..16 ASCII letters, digits, underscores or hyphens.");
        if (server.ConnectionTimeoutSeconds is < 1 or > 30 || server.CallTimeoutSeconds is < 1 or > 120)
            throw new InvalidDataException("MCP connection timeout must be 1..30 seconds and call timeout 1..120 seconds.");
        if (server.Transport == "stdio")
        {
            if (string.IsNullOrWhiteSpace(server.Command) || server.Command.Length > 4096 || server.Command.Contains('\0') ||
                server.Url is not null || server.HeaderEnvironmentVariables is { Count: > 0 })
                throw new InvalidDataException("MCP stdio requires a command and cannot configure an HTTP URL or headers.");
            if (server.Arguments is { Length: > 128 } || (server.Arguments ?? []).Any(x => x is null || x.Length > 8192 || x.Contains('\0')))
                throw new InvalidDataException("MCP command arguments exceed the configured limits.");
        }
        else if (server.Transport == "http")
        {
            if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) ||
                !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback) ||
                uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
                server.Command is not null || server.Arguments is not null || server.EnvironmentVariables is { Length: > 0 })
                throw new InvalidDataException("MCP HTTP requires HTTPS (or loopback HTTP), without URL credentials, query or fragment.");
        }
        else throw new InvalidDataException("MCP transport must be 'stdio' or 'http'.");
        if (server.EnvironmentVariables is { Length: > 32 } || server.HeaderEnvironmentVariables is { Count: > 16 })
            throw new InvalidDataException("MCP environment/header lists exceed their configured limits.");
        foreach (var variable in (server.EnvironmentVariables ?? []).Concat((server.HeaderEnvironmentVariables ?? []).Values))
            if (variable is null || !Regex.IsMatch(variable, "^[a-zA-Z_][a-zA-Z0-9_]{0,127}$", RegexOptions.CultureInvariant) || excluded.Contains(variable))
                throw new InvalidDataException("MCP environment references must be variable names and cannot reference model credentials.");
        foreach (var header in (server.HeaderEnvironmentVariables ?? []).Keys)
            if (!Regex.IsMatch(header, "^[a-zA-Z0-9!#$%&'*+.^_`|~-]{1,64}$", RegexOptions.CultureInvariant) ||
                header.StartsWith("Mcp-", StringComparison.OrdinalIgnoreCase) ||
                new[] { "Host", "Content-Length", "Content-Type", "Accept", "Connection", "Transfer-Encoding" }.Contains(header, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("MCP header name is invalid or controlled by the transport.");
    }

    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidDataException($"MCP environment variable '{name}' is not set.");

    private static string LocalName(string id, string name)
    {
        var readable = Regex.Replace(name, "[^a-zA-Z0-9_-]", "_");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id + "\0" + name)))[..12];
        return $"mcp_{id}__{Clip(readable, 24)}_{hash}";
    }

    private static string Clip(string value, int limit)
    {
        if (value.Length <= limit) return value;
        if (limit > 0 && char.IsHighSurrogate(value[limit - 1])) limit--;
        return value[..limit];
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new(_disposeTask ??= Task.Run(DisposeCoreAsync));
    }

    private async Task DisposeAfterCancellationAsync()
    {
        try { await DisposeAsync(); } catch { /* Cancellation must still close all remaining clients. */ }
    }

    private async Task DisposeCoreAsync()
    {
        _runCancellation.Unregister();
        await _shutdown.CancelAsync();
        // SDK session DELETE uses CancellationToken.None. Bound that shutdown request using the
        // owned BCL HttpClients, so an unresponsive remote endpoint cannot hold the run open.
        using var httpShutdownDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var forceHttpClose = httpShutdownDeadline.Token.UnsafeRegister(static state =>
        {
            foreach (var httpClient in (List<HttpClient>)state!) httpClient.Dispose();
        }, _httpClients);
        List<Exception> errors = [];
        foreach (var client in _clients.AsEnumerable().Reverse())
            try { await client.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        foreach (var transport in _transports.AsEnumerable().Reverse())
            try { await transport.DisposeAsync(); } catch (Exception ex) { errors.Add(ex); }
        foreach (var httpClient in _httpClients) httpClient.Dispose();
        if (errors.Count > 0) throw new AggregateException("MCP cleanup failed.", errors);
    }

    private sealed class RemoteTool(McpToolSet owner, McpClient client, McpServerConfiguration server,
        string workspace, string remoteName, ToolDefinition definition) : IAgentTool
    {
        public ToolDefinition Definition => definition;

        public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            if (!Path.GetFullPath(context.WorkspacePath).Equals(workspace,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return new("MCP tools cannot be reused in a different workspace.", true);
            if (call.Name != Definition.Name || call.ArgumentsJson.Length > MaximumSchemaCharacters)
                return new("Invalid MCP tool name or arguments exceed 32768 characters.", true);
            Dictionary<string, JsonElement>? arguments;
            try
            {
                arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(call.ArgumentsJson,
                    new JsonSerializerOptions { MaxDepth = 32, AllowDuplicateProperties = false });
                if (arguments is null) return new("MCP arguments must be a JSON object.", true);
            }
            catch (JsonException) { return new("MCP arguments must be a JSON object without duplicate properties.", true); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, owner._shutdown.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(server.CallTimeoutSeconds));
            try
            {
                var result = await client.CallToolAsync(new CallToolRequestParams { Name = remoteName, Arguments = arguments }, deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                return ProjectResult(server.Id, remoteName, result);
            }
            catch (OperationCanceledException)
            {
                // An action may already have happened remotely. Close this run's clients; never replay it.
                await owner.DisposeAfterCancellationAsync();
                if (cancellationToken.IsCancellationRequested) throw;
                return new("MCP call timed out or the run ended; remote outcome is unknown. The connection was closed; the action was not retried.", true);
            }
            catch (McpException ex)
            {
                return new(JsonSerializer.Serialize(new { server = server.Id, tool = remoteName,
                    protocolError = Clip(ex.Message, 4096), outcome = "unknown", retried = false }, Wire.Json), true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new($"MCP request failed ({ex.GetType().Name}); remote outcome is unknown. The action was not retried.", true);
            }
        }
    }

    private static ToolResult ProjectResult(string server, string tool, CallToolResult result)
    {
        var blocks = new JsonArray();
        var truncated = result.Content.Count > 32;
        foreach (var block in result.Content.Take(32))
        {
            JsonObject projected = block switch
            {
                TextContentBlock text => new() { ["type"] = "text", ["text"] = Clip(text.Text, 8192), ["truncated"] = text.Text.Length > 8192 },
                ImageContentBlock image => new() { ["type"] = "image", ["mimeType"] = Clip(image.MimeType, 128), ["base64Bytes"] = image.Data.Length, ["dataOmitted"] = true },
                AudioContentBlock audio => new() { ["type"] = "audio", ["mimeType"] = Clip(audio.MimeType, 128), ["base64Bytes"] = audio.Data.Length, ["dataOmitted"] = true },
                ResourceLinkBlock link => new() { ["type"] = "resource_link", ["uri"] = Clip(link.Uri, 2048), ["name"] = Clip(link.Name, 256), ["mimeType"] = Clip(link.MimeType ?? "", 128), ["size"] = link.Size },
                EmbeddedResourceBlock resource when resource.Resource is TextResourceContents text => new()
                    { ["type"] = "resource", ["uri"] = Clip(text.Uri, 2048), ["text"] = Clip(text.Text, 8192), ["truncated"] = text.Text.Length > 8192 },
                EmbeddedResourceBlock resource => new() { ["type"] = "resource", ["uri"] = Clip(resource.Resource.Uri, 2048), ["dataOmitted"] = true },
                _ => new() { ["type"] = Clip(block.Type, 128), ["contentOmitted"] = true }
            };
            if (projected["truncated"]?.GetValue<bool>() == true) truncated = true;
            blocks.Add(projected);
        }
        JsonNode? structured = null;
        if (result.StructuredContent is { } content)
        {
            var raw = content.GetRawText();
            if (raw.Length <= 8192) structured = JsonNode.Parse(raw);
            else { structured = new JsonObject { ["omitted"] = true, ["reason"] = "structured content exceeds 8192 characters" }; truncated = true; }
        }
        var response = new JsonObject { ["server"] = server, ["tool"] = tool, ["isError"] = result.IsError ?? false,
            ["content"] = blocks, ["structuredContent"] = structured, ["truncated"] = truncated };
        var serialized = response.ToJsonString(Wire.Json);
        while (serialized.Length > MaximumResultCharacters && blocks.Count > 0)
        {
            blocks.RemoveAt(blocks.Count - 1);
            response["truncated"] = true;
            serialized = response.ToJsonString(Wire.Json);
        }
        if (serialized.Length > MaximumResultCharacters)
        {
            response["structuredContent"] = new JsonObject { ["omitted"] = true, ["reason"] = "escaped structured content exceeds result limit" };
            response["truncated"] = true;
            serialized = response.ToJsonString(Wire.Json);
        }
        return new(serialized, result.IsError ?? false);
    }
}
