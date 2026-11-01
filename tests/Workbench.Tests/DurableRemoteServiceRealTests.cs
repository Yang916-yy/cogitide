using System.Diagnostics;
using System.Text.Json;
using Workbench.Desktop;
using Workbench.Desktop.Remote;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

/// <summary>Separately opted-in real WSL/system-SSH daemon lifecycle; no model/account calls.</summary>
public sealed class DurableRemoteServiceRealTests
{
    [DurableRemoteServiceRealFact]
    public async Task ActualDesktopAttachmentDetachesWhileOwnedCommandContinuesAndExplicitServiceStopCleansUp()
    {
        var kind = Enum.Parse<HostConnectionKind>(Required("KIND"), true);
        var workspace = Required("WORKSPACE");
        var nativeRoot = RemoteServiceFixturePaths.NativeRoot(Required("DATA_DIRECTORY"), Environment.GetEnvironmentVariable("WORKBENCH_REMOTE_REAL_SERVICE_ROOT"));
        var state = nativeRoot + "/durable-service-" + Guid.NewGuid().ToString("N");
        Assert.Contains("/cogitide-remote-check-", workspace, StringComparison.Ordinal);
        Assert.Contains("/cogitide-remote-check-", state, StringComparison.Ordinal);
        var profile = new HostConnectionProfile
        {
            Kind = kind, Destination = Required("DESTINATION"), HostPath = Required("HOST_PATH"), DataDirectory = state,
            DotnetPath = Environment.GetEnvironmentVariable("WORKBENCH_REMOTE_REAL_DOTNET_PATH") ?? "dotnet"
        };
        var config = Environment.GetEnvironmentVariable("WORKBENCH_REMOTE_TEST_SSH_CONFIG");
        if (kind == HostConnectionKind.Wsl) await RemoteServiceFixturePaths.EnsureWslRootAsync(profile, nativeRoot);
        ProcessStartInfo Configure(ProcessStartInfo start)
        {
            if (!string.IsNullOrWhiteSpace(config)) { Assert.True(File.Exists(config)); start.ArgumentList.Insert(0, config); start.ArgumentList.Insert(0, "-F"); }
            return start;
        }
        HostClient Client() => new(connection => Configure(RemoteHostLaunch.CreateStartInfo(connection)));
        IReadOnlyList<string> Arguments(params string[] command) => (profile.HostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new[] { profile.DotnetPath, profile.HostPath } : [profile.HostPath]).Concat(command).ToArray();
        async Task<string> Native(IReadOnlyList<string> arguments)
        {
            using var process = Process.Start(Configure(RemoteHostLaunch.CreateCommandStartInfo(profile, arguments)))!;
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch { ProcessShutdown.Terminate(process); throw; }
            var result = await stdout; await stderr;
            Assert.True(process.ExitCode == 0, "The explicit real service command failed; inspect endpoint-local fixture logs. Raw diagnostics are not persisted.");
            return result;
        }
        var identity = JsonSerializer.Deserialize<RemoteServiceIdentity>(await Native(Arguments("--service-start", "--data-dir", state)), Wire.Json)!;
        profile = profile with { DurableService = true, ServiceId = identity.ServiceId };
        var stopped = false;
        try
        {
            var marker = "durable-" + Guid.NewGuid().ToString("N") + ".txt";
            Task<ToolResult> command;
            await using (var first = Client())
            {
                await first.StartAsync(profile);
                var initialize = await first.RequestAsync<JsonElement>("initialize");
                Assert.Equal(identity, initialize.GetProperty("remoteService").Deserialize<RemoteServiceIdentity>(Wire.Json));
                command = first.RequestAsync<ToolResult>("workspace.command", new
                {
                    workspacePath = workspace,
                    command = $"""printf '%s\n' started > {marker}; sleep 2; printf '%s\n' completed-once >> {marker}""",
                    timeoutSeconds = 20
                });
                await WaitForFile(first, workspace, marker, "started\n");
            }
            await Assert.ThrowsAnyAsync<Exception>(() => command);
            await using var second = Client();
            await second.StartAsync(profile);
            var reconnected = await second.RequestAsync<JsonElement>("initialize");
            Assert.Equal(identity, reconnected.GetProperty("remoteService").Deserialize<RemoteServiceIdentity>(Wire.Json));
            await WaitForFile(second, workspace, marker, "started\ncompleted-once\n");
            Assert.Empty(await second.RequestAsync<ApprovalRequest[]>("approval.pending"));
            var disconnect = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            second.Disconnected += _ => disconnect.TrySetResult();
            var lateMarker = "stop-" + Guid.NewGuid().ToString("N") + ".txt";
            var owned = second.RequestAsync<ToolResult>("workspace.command", new
            {
                workspacePath = workspace,
                command = $"""printf '%s\n' started > {lateMarker}; sleep 8; printf '%s\n' must-not-run >> {lateMarker}""",
                timeoutSeconds = 20
            });
            await WaitForFile(second, workspace, lateMarker, "started\n");
            await Native(Arguments("--service-stop", "--data-dir", state, "--expected-service-id", identity.ServiceId));
            stopped = true;
            await disconnect.Task.WaitAsync(TimeSpan.FromSeconds(15));
            try { await owned; } catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException) { }
            // A fresh foreground owner can inspect the generated files after daemon shutdown; it executes no prior command.
            await using var inspect = Client();
            await inspect.StartAsync(profile with { DurableService = false, ServiceId = null,
                DataDirectory = Required("DATA_DIRECTORY") + "-post-service-inspect-" + Guid.NewGuid().ToString("N") });
            await WaitForFile(inspect, workspace, lateMarker, "started\n");
            var result = await inspect.RequestAsync<ToolResult>("workspace.command", new { workspacePath = workspace, command = "sleep 9; cat " + lateMarker, timeoutSeconds = 15 });
            Assert.False(result.IsError);
            Assert.Contains("started", result.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("must-not-run", result.Content, StringComparison.Ordinal);
        }
        finally
        {
            if (!stopped) await Native(Arguments("--service-stop", "--data-dir", state, "--expected-service-id", identity.ServiceId));
        }
    }

    private static async Task WaitForFile(HostClient client, string workspace, string path, string expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            // Creation is deliberately concurrent with this observer. An absent inventory entry is
            // the sole transient condition; transport/protocol/permission errors still fail the test.
            var inventory = await client.RequestAsync<ToolResult>("workspace.files.page", new { workspacePath = workspace, filter = path, limit = 1 }, deadline.Token);
            Assert.False(inventory.IsError);
            var files = JsonSerializer.Deserialize<JsonElement>(inventory.Content).GetProperty("files");
            if (files.EnumerateArray().Any(file => file.GetString() == path))
            {
                var result = await client.RequestAsync<ToolResult>("workspace.read.page", new { workspacePath = workspace, path, limit = 20 }, deadline.Token);
                Assert.False(result.IsError);
                if (JsonSerializer.Deserialize<JsonElement>(result.Content).GetProperty("content").GetString() == expected) return;
            }
            await Task.Delay(50, deadline.Token);
        }
    }
    private static string Required(string suffix) => Environment.GetEnvironmentVariable("WORKBENCH_REMOTE_REAL_" + suffix)
        ?? throw new InvalidOperationException("The opt-in generated real fixture is missing " + suffix + ".");
}

internal sealed class DurableRemoteServiceRealFactAttribute : FactAttribute
{
    public DurableRemoteServiceRealFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WORKBENCH_DURABLE_REMOTE_REAL_PROBE") != "1")
            Skip = "Detached service real acceptance is disabled. Prepare the generated verify-remote.py fixture and opt in with WORKBENCH_DURABLE_REMOTE_REAL_PROBE=1 plus WORKBENCH_REMOTE_REAL_* settings.";
    }
}
