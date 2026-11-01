using System.Diagnostics;
using System.Text.Json;
using Workbench.Desktop;
using Workbench.Desktop.Remote;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

/// <summary>Explicit WSL acceptance of the actual guided deployer using a matching prebuilt package.</summary>
public sealed class RemoteDeploymentRealTests
{
    [RemoteDeploymentRealFact]
    public async Task ActualGuidedWslDeploymentVerifiesTransfersStartsReattachesAndRefusesExistingPaths()
    {
        var package = Environment.GetEnvironmentVariable("WORKBENCH_REMOTE_DEPLOYMENT_PACKAGE")
            ?? throw new InvalidOperationException("Guided deployment opt-in requires WORKBENCH_REMOTE_DEPLOYMENT_PACKAGE.");
        Assert.True(Path.IsPathFullyQualified(package), "The prebuilt package path must be absolute.");
        Assert.True(Directory.Exists(package), "The explicitly selected prebuilt package directory does not exist.");
        var workspace = Required("WORKSPACE");
        var generatedRoot = Required("DATA_DIRECTORY");
        RemotePaths.RequireAbsolute(workspace);
        RemotePaths.RequireAbsolute(generatedRoot);
        Assert.Contains("/cogitide-remote-check-", workspace, StringComparison.Ordinal);
        Assert.Contains("/cogitide-remote-check-", generatedRoot, StringComparison.Ordinal);
        var nativeRoot = RemoteServiceFixturePaths.NativeRoot(generatedRoot, Environment.GetEnvironmentVariable("WORKBENCH_REMOTE_REAL_SERVICE_ROOT"));
        var unique = nativeRoot + "/guided-" + Guid.NewGuid().ToString("N");
        var installation = unique + " install 'quoted' 中文";
        var data = unique + " data 'quoted' 中文";
        var connection = new HostConnectionProfile
        {
            Kind = HostConnectionKind.Wsl, Destination = Required("DESTINATION"), HostPath = Required("HOST_PATH"),
            DotnetPath = Environment.GetEnvironmentVariable("WORKBENCH_REMOTE_REAL_DOTNET_PATH") ?? "dotnet"
        };
        var request = new RemoteDeploymentRequest(connection, package, installation, data);
        HostConnectionProfile? deployed = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        try
        {
            await RemoteServiceFixturePaths.EnsureWslRootAsync(connection, nativeRoot);
            var steps = new List<string>();
            deployed = await RemoteDeployment.DeployAsync(request, new CaptureProgress(steps), deadline.Token);
            Assert.True(deployed.DurableService);
            Assert.Equal(installation + "/Workbench.Host", deployed.HostPath);
            Assert.Equal(data, deployed.DataDirectory);
            Assert.True(Guid.TryParseExact(deployed.ServiceId, "N", out _));
            Assert.True(steps.Count >= 6, "The actual package verification, transfer and service startup steps did not all run.");
            string sessionId;
            RemoteServiceIdentity identity;
            await using (var first = new HostClient())
            {
                await first.StartAsync(deployed, deadline.Token);
                var initialized = await first.RequestAsync<JsonElement>("initialize", cancellationToken: deadline.Token);
                identity = initialized.GetProperty("remoteService").Deserialize<RemoteServiceIdentity>(Wire.Json)!;
                Assert.Equal(deployed.ServiceId, identity.ServiceId);
                Assert.Equal("linux", initialized.GetProperty("platform").GetString());
                var capabilities = initialized.GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()).ToArray();
                foreach (var capability in new[] { "session.events.since", "approval.pending", "isolation.status", "workspace.review", "provider.presets", "provider.models" })
                    Assert.Contains(capability, capabilities);
                var created = await first.RequestAsync<AgentSession>("session.create", new
                {
                    workspacePath = workspace, title = "Generated guided deployment acceptance", backend = "api"
                }, deadline.Token);
                sessionId = created.Info.Id;
            }
            await using var second = new HostClient();
            await second.StartAsync(deployed, deadline.Token);
            var reattached = await second.RequestAsync<JsonElement>("initialize", cancellationToken: deadline.Token);
            Assert.Equal(identity, reattached.GetProperty("remoteService").Deserialize<RemoteServiceIdentity>(Wire.Json));
            var restored = await second.RequestAsync<SessionView>("session.view", new { sessionId, limit = 5 }, deadline.Token);
            Assert.Equal(workspace, restored.Info.WorkspacePath);
            Assert.Empty(restored.Messages);
            Assert.Equal(0, restored.MessageEventSequence);
            // Repeating the explicit operation must fail before transfer and leave the verified owner intact.
            await Assert.ThrowsAsync<IOException>(() => RemoteDeployment.DeployAsync(request, ct: deadline.Token));
            Assert.Equal(identity, await second.RequestAsync<RemoteServiceIdentity>("remote.service.status", cancellationToken: deadline.Token));
        }
        finally
        {
            // Retain generated directories and package evidence. Stop only this generated daemon, even
            // if deployment reached startup but its identity reply was lost. Never delete a remote tree.
            if (deployed is null) deployed = await DiscoverGeneratedServiceAsync(connection, installation, data);
            if (deployed is not null) await RemoteDeployment.StopAsync(deployed);
        }
    }

    private static async Task<HostConnectionProfile?> DiscoverGeneratedServiceAsync(HostConnectionProfile connection, string installation, string data)
    {
        var endpoint = await ProbeAsync(RemoteDeployment.Command(connection, ["/bin/sh", "-c", "if test -e " +
            RemoteHostLaunch.QuotePosixArgument(data + "/remote-service/endpoint.json") + "; then printf present; else printf absent; fi"]));
        Assert.True(endpoint.ExitCode == 0 && endpoint.Output is "present" or "absent",
            "The generated endpoint's final presence could not be observed; cleanup outcome is unknown.");
        if (endpoint.Output == "absent") return null;
        var identity = await ProbeAsync(RemoteDeployment.Command(connection, ["cat", data + "/remote-service/identity"]));
        Assert.True(identity.ExitCode == 0 && Guid.TryParseExact(identity.Output.Trim(), "N", out _),
            "The generated deployment endpoint exists but its cleanup identity could not be verified.");
        return connection with { HostPath = installation + "/Workbench.Host", DataDirectory = data, DurableService = true, ServiceId = identity.Output.Trim() };
    }

    private static async Task<(int ExitCode, string Output)> ProbeAsync(ProcessStartInfo start)
    {
        using var process = Process.Start(start) ?? throw new IOException("The explicit WSL fixture probe could not start.");
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var diagnostics = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { ProcessShutdown.Terminate(process); throw; }
        var value = await output; await diagnostics;
        Assert.InRange(value.Length, 0, 128); // Only a generated nonsecret 32-character identity can be read.
        return (process.ExitCode, value);
    }

    private sealed class CaptureProgress(ICollection<string> steps) : IProgress<string>
    {
        public void Report(string value) => steps.Add(value);
    }

    private static string Required(string suffix) => Environment.GetEnvironmentVariable("WORKBENCH_REMOTE_REAL_" + suffix)
        ?? throw new InvalidOperationException("The explicitly generated guided deployment fixture is missing " + suffix + ".");
}

internal sealed class RemoteDeploymentRealFactAttribute : FactAttribute
{
    public RemoteDeploymentRealFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WORKBENCH_DURABLE_REMOTE_REAL_PROBE") != "1")
            Skip = "Guided deployment real acceptance is disabled. Opt in with WORKBENCH_DURABLE_REMOTE_REAL_PROBE=1, generated WORKBENCH_REMOTE_REAL_* settings and WORKBENCH_REMOTE_DEPLOYMENT_PACKAGE.";
        else if (!string.Equals(Environment.GetEnvironmentVariable("WORKBENCH_REMOTE_REAL_KIND"), "Wsl", StringComparison.OrdinalIgnoreCase))
            Skip = "Guided deployment acceptance targets the explicit WSL fixture. Loopback SSH service lifecycle is covered separately; this test does not enable generic SCP/SFTP on that fixture.";
    }
}
