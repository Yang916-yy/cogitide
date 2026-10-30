using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Desktop;
using Workbench.Desktop.Remote;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

/// <summary>Explicit real transport acceptance; the ordinary suite reports a skipped test.</summary>
public sealed class RealRemoteConnectionTests
{
    private const string ProbeContent = "Cogitide real remote fixture.\nUnicode 中文 😀 stays intact.\nThird line.\n";

    [RealRemoteFact]
    public async Task ActualDesktopHostClientReadsPersistsAndReconnectsThroughTheSelectedTransport()
    {
        var kind = Enum.Parse<HostConnectionKind>(Required("KIND"), ignoreCase: true);
        Assert.True(kind is HostConnectionKind.Ssh or HostConnectionKind.Wsl);
        var workspace = Required("WORKSPACE");
        var dataDirectory = Required("DATA_DIRECTORY");
        RemotePaths.RequireAbsolute(workspace);
        RemotePaths.RequireAbsolute(dataDirectory);
        // This opt-in must use the generated fixture, never a personal account/data store.
        Assert.Contains("/cogitide-remote-check-", workspace, StringComparison.Ordinal);
        Assert.Contains("/cogitide-remote-check-", dataDirectory, StringComparison.Ordinal);
        var profile = new HostConnectionProfile
        {
            Id = "actual-transport-fixture", Name = "Explicit generated remote acceptance", Kind = kind,
            Destination = Required("DESTINATION"), HostPath = Required("HOST_PATH"),
            DotnetPath = Environment.GetEnvironmentVariable("WORKBENCH_REMOTE_REAL_DOTNET_PATH") ?? "dotnet",
            DataDirectory = dataDirectory
        };
        var testConfig = Environment.GetEnvironmentVariable("WORKBENCH_REMOTE_TEST_SSH_CONFIG");
        HostClient CreateClient() => string.IsNullOrWhiteSpace(testConfig) ? new() : new(connection =>
        {
            Assert.Equal(HostConnectionKind.Ssh, connection.Kind);
            Assert.True(File.Exists(testConfig), "The explicit ephemeral SSH fixture configuration no longer exists.");
            var start = RemoteHostLaunch.CreateStartInfo(connection);
            start.ArgumentList.Insert(0, testConfig);
            start.ArgumentList.Insert(0, "-F");
            return start;
        });

        await using var first = CreateClient();
        await first.StartAsync(profile);
        Assert.True(first.Connected && first.IsRemote);
        var initialization = await first.RequestAsync<JsonElement>("initialize", new { protocolVersion = Wire.Version });
        Assert.Equal("linux", initialization.GetProperty("platform").GetString());
        var validated = await first.RequestAsync<JsonElement>("workspace.validate", new { workspacePath = workspace });
        Assert.Equal(workspace, validated.GetProperty("workspacePath").GetString());
        var files = new List<string>();
        string? snapshot = null;
        var offset = 0;
        for (var index = 0; index < 16; index++)
        {
            var result = await first.RequestAsync<ToolResult>("workspace.files.page", new { workspacePath = workspace, offset, limit = 1, expectedSnapshot = snapshot });
            Assert.False(result.IsError);
            var page = JsonSerializer.Deserialize<JsonElement>(result.Content);
            snapshot ??= page.GetProperty("snapshot").GetString();
            Assert.Equal(snapshot, page.GetProperty("snapshot").GetString());
            files.AddRange(page.GetProperty("files").EnumerateArray().Select(item => item.GetString()!));
            if (page.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
            var next = page.GetProperty("nextOffset").GetInt32();
            Assert.True(next > offset);
            offset = next;
        }
        Assert.Contains("probe 'quoted' 中文.txt", files);
        var read = await first.RequestAsync<ToolResult>("workspace.read.page", new { workspacePath = workspace, path = "probe 'quoted' 中文.txt", limit = 1 });
        Assert.False(read.IsError);
        var document = JsonSerializer.Deserialize<JsonElement>(read.Content);
        Assert.Equal("Cogitide real remote fixture.\n", document.GetProperty("content").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ProbeContent))), document.GetProperty("sha256").GetString());
        var created = await first.RequestAsync<JsonElement>("session.create", new { workspacePath = workspace, title = "Actual HostClient generated fixture", backend = "api" });
        var sessionId = created.GetProperty("info").GetProperty("id").GetString();
        var command = await first.RequestAsync<ToolResult>("workspace.command", new
        {
            workspacePath = workspace, command = "printf '%s\\n' HOSTCLIENT_REAL_ADAPTER_OK", timeoutSeconds = 10
        });
        Assert.False(command.IsError);
        Assert.Contains("HOSTCLIENT_REAL_ADAPTER_OK", command.Content, StringComparison.Ordinal);
        await first.DisposeAsync();
        Assert.False(first.Connected);

        await using var second = CreateClient();
        await second.StartAsync(profile);
        var restored = await second.RequestAsync<SessionView>("session.view", new { sessionId, limit = 5 });
        Assert.True(restored.Available);
        Assert.Equal(workspace, restored.Info.WorkspacePath);
        Assert.Empty(restored.Messages);
        Assert.Null(restored.RunCheckpoint);
        Assert.Equal("idle", restored.Info.Status);
    }

    private static string Required(string suffix) => Environment.GetEnvironmentVariable("WORKBENCH_REMOTE_REAL_" + suffix)
        ?? throw new InvalidOperationException("The explicitly enabled real remote fixture is missing WORKBENCH_REMOTE_REAL_" + suffix + ".");
}

internal sealed class RealRemoteFactAttribute : FactAttribute
{
    public RealRemoteFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WORKBENCH_REMOTE_REAL_PROBE") != "1")
            Skip = "Explicit real remote acceptance is disabled. Prepare scripts/verify-remote.py fixture and set its WORKBENCH_REMOTE_REAL_* environment to opt in.";
    }
}
