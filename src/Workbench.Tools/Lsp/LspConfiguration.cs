using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workbench.Tools;

/// <summary>Explicit user-selected configuration; never discovered in repository content.</summary>
public sealed record LspWorkspaceConfiguration(LspServerConfiguration[] Servers);

/// <summary>Launching a language server grants it the user's privileges in a trusted project.</summary>
public sealed record LspServerConfiguration(string Id, string Command, string[] Arguments, string[] Languages,
    string Root = ".", bool TrustedWorkspace = false, JsonElement? InitializationOptions = null,
    JsonElement? Settings = null, string[]? EnvironmentVariables = null, int RequestTimeoutSeconds = 30);

public sealed partial class LspToolSet
{
    private static readonly JsonSerializerOptions ConfigJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false, MaxDepth = 16
    };

    /// <summary>Reads an externally selected configuration without starting a process. Absence disables LSP.</summary>
    public static async Task<LspToolSet> LoadAsync(string configPath, string workspace,
        IReadOnlyList<string>? excludedEnvNames = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var configuration = new LspWorkspaceConfiguration([]);
        if (File.Exists(configPath))
        {
            await using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var bytes = new byte[65_537];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, ct);
            if (count > 65_536) throw new InvalidDataException("LSP configuration exceeds 64 KiB.");
            configuration = JsonSerializer.Deserialize<LspWorkspaceConfiguration>(bytes.AsSpan(0, count), ConfigJson)
                ?? throw new InvalidDataException("LSP configuration must be an object.");
        }
        ct.ThrowIfCancellationRequested();
        return new(workspace, configuration, excludedEnvNames, ct);
    }

    private static void Validate(LspWorkspaceConfiguration configuration, string workspace)
    {
        if (configuration.Servers is null || configuration.Servers.Length > 8)
            throw new InvalidDataException("LSP configuration requires at most 8 servers.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var server in configuration.Servers)
        {
            if (server is null || string.IsNullOrWhiteSpace(server.Id) || server.Id.Length > 64 ||
                server.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-') || !ids.Add(server.Id))
                throw new InvalidDataException("LSP server IDs must be unique ASCII identifiers.");
            if (!Path.IsPathFullyQualified(server.Command) || !File.Exists(server.Command) ||
                server.Command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                server.Command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("LSP command must be an existing absolute executable path; use node plus a server .js path instead of a shell shim.");
            if (!server.TrustedWorkspace)
                throw new InvalidDataException("LSP servers require explicit trustedWorkspace=true; they may load project plugins, analyzers or build configuration with user privileges.");
            if (server.Arguments is null || server.Arguments.Length > 64 || server.Arguments.Any(a => a is null || a.Length > 8192 || a.Contains('\0')) ||
                server.Languages is null || server.Languages.Length is < 1 or > 16 || server.Languages.Any(l => !Languages.Contains(l)) ||
                server.RequestTimeoutSeconds is < 1 or > 120 || server.EnvironmentVariables is { Length: > 32 })
                throw new InvalidDataException("Invalid LSP arguments, languages, environment or request timeout.");
            if (!Directory.Exists(WorkspaceService.Resolve(workspace, server.Root, true)))
                throw new InvalidDataException("LSP root must be an existing workspace-relative directory.");
            foreach (var name in server.EnvironmentVariables ?? [])
                if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
                    throw new InvalidDataException("Invalid LSP environment variable name.");
        }
    }
}
