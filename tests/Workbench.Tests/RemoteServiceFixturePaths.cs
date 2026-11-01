using System.Diagnostics;
using Workbench.Desktop;
using Workbench.Desktop.Remote;
using Xunit;

namespace Workbench.Tests;

/// <summary>Only generated fixtures use /tmp; mounted Windows workspaces retain their original paths.</summary>
internal static class RemoteServiceFixturePaths
{
    internal static string NativeRoot(string fixturePath, string? configured = null)
    {
        RemotePaths.RequireAbsolute(fixturePath);
        const string prefix = "cogitide-remote-check-";
        var roots = fixturePath.Split('/').Where(part => part.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (roots.Length != 1 || !Guid.TryParseExact(roots[0][prefix.Length..], "N", out _))
            throw new ArgumentException("The native service fixture must derive from exactly one generated remote-check GUID.");
        var expected = "/tmp/" + roots[0];
        if (configured is not null && configured != expected) throw new ArgumentException("The configured native fixture root does not match its generated GUID.");
        return expected;
    }

    internal static async Task EnsureWslRootAsync(HostConnectionProfile connection, string root)
    {
        if (connection.Kind != HostConnectionKind.Wsl) throw new ArgumentException("Only the explicit WSL fixture can create its native private parent.");
        var path = RemoteHostLaunch.QuotePosixArgument(root);
        var script = "umask 077; if test ! -e " + path + " && test ! -L " + path + "; then mkdir -- " + path + "; fi; " +
            "test -d " + path + " && test ! -L " + path + " && test -O " + path + " && test \"$(stat -c '%a' " + path + ")\" = 700";
        using var process = Process.Start(RemoteDeployment.Command(connection, ["/bin/sh", "-c", script]))!;
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = process.StandardError.ReadToEndAsync(deadline.Token);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { ProcessShutdown.Terminate(process); throw; }
        await output; await errors;
        Assert.True(process.ExitCode == 0, "The generated Linux-native service parent could not be verified as a private, owned, non-symlink directory.");
    }
}

public sealed class RemoteServiceFixturePathTests
{
    [Fact]
    public void NativeParentDerivesOnlyFromTheSingleGeneratedFixtureGuid()
    {
        const string id = "bc0a97e9aa394bfdb9f04f6b8e8954fd";
        var source = "/mnt/d/agent/artifacts/remote-checks/cogitide-remote-check-" + id + "/adapter-state";
        Assert.Equal("/tmp/cogitide-remote-check-" + id, RemoteServiceFixturePaths.NativeRoot(source));
        Assert.Throws<ArgumentException>(() => RemoteServiceFixturePaths.NativeRoot(source, "/tmp/unrelated"));
        Assert.Throws<ArgumentException>(() => RemoteServiceFixturePaths.NativeRoot("/home/personal-state"));
        Assert.Throws<ArgumentException>(() => RemoteServiceFixturePaths.NativeRoot("/tmp/cogitide-remote-check-not-a-guid/state"));
        Assert.Throws<ArgumentException>(() => RemoteServiceFixturePaths.NativeRoot(source + "/cogitide-remote-check-" + id));
    }
}
