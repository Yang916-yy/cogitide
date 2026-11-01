using System.Diagnostics;
using Microsoft.Mxc.Sdk.V1;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class OsIsolationPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(OsIsolationTestPaths.PhysicalTemporaryDirectory(), "cogitide-isolation-policy-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace;
    private readonly string _scratch;
    private readonly string _host;
    public OsIsolationPolicyTests()
    {
        Directory.CreateDirectory(_root);
        _workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        _scratch = Directory.CreateDirectory(Path.Combine(_root, "scratch")).FullName;
        _host = Directory.CreateDirectory(Path.Combine(_root, "host-state")).FullName;
    }
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void NativePolicyDoesNotInheritSecretsOrGrantTheHostDirectory()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        start.ArgumentList.Add("--tool-worker");
        start.Environment["CUSTOM_PROVIDER_KEY"] = "must-not-cross-the-boundary";
        start.Environment["DOTNET_STARTUP_HOOKS"] = "host-injection.dll";
        var request = OsIsolationPolicy.Create(start, _workspace, _scratch, _host);
        var environment = Assert.IsType<Dictionary<string, string>>(request.Environment);
        var network = Assert.IsType<NetworkPolicy>(request.Network);
        var egress = Assert.IsType<NetworkEgressPolicy>(network.Egress);
        var ingress = Assert.IsType<NetworkIngressPolicy>(network.Ingress);
        var filesystem = Assert.IsType<FilesystemPolicy>(request.Filesystem);
        if (OperatingSystem.IsWindows())
        {
            var container = Assert.IsType<Containment.ProcessContainer>(request.Containment);
            var containedUi = Assert.IsType<ProcessContainerUiPolicy>(container.Ui);
            Assert.Equal(ProcessContainerUiIsolation.Container, containedUi.Isolation);
            Assert.False(containedUi.DesktopSystemControl);
            Assert.Equal(ProcessContainerSystemSettings.None, containedUi.SystemSettings);
            Assert.False(containedUi.Ime);
            Assert.Empty(container.Capabilities);
            Assert.False(container.LearningMode);
            Assert.Null(container.CaptureDenials);
        }
        else Assert.IsType<Containment.Process>(request.Containment);
        Assert.False(request.InheritDefaultEnvironment);
        Assert.DoesNotContain("CUSTOM_PROVIDER_KEY", environment.Keys);
        Assert.DoesNotContain("DOTNET_STARTUP_HOOKS", environment.Keys);
        Assert.Equal(_scratch, environment["HOME"]);
        Assert.Equal(_scratch, environment["LOCALAPPDATA"]);
        Assert.Equal(NetworkAction.Deny, egress.Default);
        Assert.Equal(NetworkAction.Deny, ingress.Default);
        Assert.Equal(NetworkAction.Deny, ingress.HostLoopback);
        Assert.Contains(_host, filesystem.DeniedPaths);
        Assert.DoesNotContain(_host, filesystem.ReadonlyPaths);
        Assert.Equal(new[] { _workspace, _scratch }, filesystem.ReadwritePaths);
        if (OperatingSystem.IsLinux()) Assert.Null(request.Ui);
        else
        {
            var ui = Assert.IsType<UiPolicy>(request.Ui);
            Assert.Equal(!OperatingSystem.IsWindows(), ui.Disable);
            Assert.Equal(ClipboardPolicy.None, ui.Clipboard);
            Assert.False(ui.AllowInputInjection);
        }
    }

    [Fact]
    public void HostStateAndScratchCannotBeSelectedAsAWorkspace()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!);
        Assert.Throws<UnauthorizedAccessException>(() => OsIsolationPolicy.Create(start, _host, _scratch, _host));
        Assert.Throws<UnauthorizedAccessException>(() => OsIsolationPolicy.Create(start, _scratch, _scratch, _host));
        var nested = Directory.CreateDirectory(Path.Combine(_host, "nested")).FullName;
        Assert.Throws<UnauthorizedAccessException>(() => OsIsolationPolicy.Create(start, nested, _scratch, _host));
    }

    [Fact]
    public void ReadOnlyModeIsEnforcedInTheNativeFilesystemGrant()
    {
        var request = OsIsolationPolicy.Create(new(Environment.ProcessPath!), _workspace, _scratch, _host, ExecutionMode.ReadOnly);
        var filesystem = Assert.IsType<FilesystemPolicy>(request.Filesystem);
        Assert.Contains(_workspace, filesystem.ReadonlyPaths);
        Assert.DoesNotContain(_workspace, filesystem.ReadwritePaths);
        Assert.Equal(new[] { _scratch }, filesystem.ReadwritePaths);
    }

    [Fact]
    public async Task NormalFactoryAndToolsRetainTheirExistingPath()
    {
        var factoryCalls = 0;
        await using var manager = new OsIsolationManager(() => throw new InvalidOperationException("Native launch must not occur."), _host,
            (workspace, excluded, ct) => { factoryCalls++; return Task.FromResult<IAgentToolSet?>(null); });
        var context = new ToolExecutionContext(_workspace, SessionId: "chat", RunId: "run");
        Assert.Null(await manager.CreateRunToolsAsync(context));
        Assert.Equal(1, factoryCalls);
        var tool = new FixtureTool();
        var result = await manager.ExecuteAsync(tool, new("1", "fixture", "{}"), context);
        Assert.Equal("executed-once", result.Content);
        Assert.Equal(1, tool.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReadWorkspaceTextAsync(_workspace, "AGENTS.md", 1024));
    }

    [Fact]
    public async Task RequiredIsolationNeverFallsBackToTheOriginalTool()
    {
        await using var manager = new OsIsolationManager(() => throw new InvalidOperationException("not initialized"), _host);
        var tool = new FixtureTool();
        var context = new ToolExecutionContext(_workspace, SessionId: "chat", RunId: "run", RequireOsIsolation: true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ExecuteAsync(tool, new("1", "fixture", "{}"), context));
        Assert.Equal(0, tool.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => manager.CreateRunToolsAsync(context with { SessionId = null }));
    }

    private sealed class FixtureTool : IAgentTool
    {
        public int Calls;
        public ToolDefinition Definition => new("fixture", "", "{}");
        public Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new ToolResult("executed-once")); }
    }
}

internal static class OsIsolationTestPaths
{
    internal static string PhysicalTemporaryDirectory()
    {
        var path = Path.GetTempPath();
        return OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + path : path;
    }
}
