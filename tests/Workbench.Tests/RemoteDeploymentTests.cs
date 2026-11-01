using System.Security.Cryptography;
using System.Text.Json;
using Workbench.Desktop.Remote;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class RemoteDeploymentTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-deployment-" + Guid.NewGuid().ToString("N"));
    public RemoteDeploymentTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ExistingPublishManifestVerifiesEveryAssetMatchingHostAndProtocolBuild()
    {
        var hash = await Package();
        var package = await RemoteDeploymentPackage.ValidateAsync(_root, hash);
        Assert.Equal("linux-x64", package.Runtime);
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteDeploymentPackage.ValidateAsync(_root, new string('0', 64)));
        await File.AppendAllTextAsync(Path.Combine(_root, "Workbench.Host"), "corruption");
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteDeploymentPackage.ValidateAsync(_root, hash));
    }

    [Fact]
    public async Task PublishRejectsExtraFilesMissingAssetsIncompletePublishAndTraversalManifest()
    {
        var hash = await Package();
        var extra = Path.Combine(_root, "credentials-must-never-copy.json");
        await File.WriteAllTextAsync(extra, "synthetic fixture only");
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteDeploymentPackage.ValidateAsync(_root, hash));
        File.Delete(extra);
        var marker = Path.Combine(_root, ".publish-incomplete");
        File.WriteAllText(marker, "incomplete");
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteDeploymentPackage.ValidateAsync(_root, hash));
        File.Delete(marker);
        File.Delete(Path.Combine(_root, "Workbench.Host"));
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteDeploymentPackage.ValidateAsync(_root, hash));
        await File.AppendAllTextAsync(Path.Combine(_root, "SHA256SUMS"), new string('0', 64) + "  ../outside\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => RemoteDeploymentPackage.ValidateAsync(_root, hash));
    }

    [Fact]
    public void DurableServiceProfilePinsIdentityAndLaunchesOnlyForegroundAttachment()
    {
        var profile = Profile() with { DurableService = true, ServiceId = Guid.NewGuid().ToString("N"), DataDirectory = "/home/user/new data" };
        var launch = RemoteHostLaunch.CreateStartInfo(profile);
        Assert.Contains("--service-bridge", launch.ArgumentList[^1]);
        Assert.Contains(profile.ServiceId!, launch.ArgumentList[^1]);
        Assert.DoesNotContain("--service-start", launch.ArgumentList[^1]);
        Assert.DoesNotContain("--service'", launch.ArgumentList[^1]);
        Assert.Throws<ArgumentException>(() => (profile with { ServiceId = null }).Validate());
        Assert.Throws<ArgumentException>(() => (profile with { DataDirectory = null }).Validate());
        Assert.DoesNotContain("token", JsonSerializer.Serialize(profile, Wire.Json), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScpRetainsStrictHostCheckingAndTreatsTransferPathsAsSingleArguments()
    {
        var archive = Path.Combine(_root, "a 'quoted' package.tar");
        var transfer = RemoteDeployment.Scp(Profile(), archive, "/home/user/host 'quoted'/package.tar");
        Assert.Equal("scp", transfer.FileName);
        Assert.Contains("StrictHostKeyChecking=yes", transfer.ArgumentList);
        Assert.Contains("BatchMode=yes", transfer.ArgumentList);
        Assert.Equal(archive, transfer.ArgumentList[^2]);
        Assert.Equal("fixture-host:/home/user/host 'quoted'/package.tar", transfer.ArgumentList[^1]);
        Assert.DoesNotContain(transfer.ArgumentList, value => value.Contains("StrictHostKeyChecking=no", StringComparison.Ordinal) || value.Contains("accept-new", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Linux\naarch64\n", "linux-arm64")]
    [InlineData("Darwin\r\nx86_64\r\n", "osx-x64")]
    public void NativeEndpointRidIsExplicit(string response, string expected) => Assert.Equal(expected, RemoteDeployment.ParseRuntime(response));

    private static HostConnectionProfile Profile() => new() { Kind = HostConnectionKind.Ssh, Destination = "fixture-host", HostPath = "/home/user/Workbench.Host" };

    private async Task<string> Package()
    {
        foreach (var name in new[] { "Workbench.Host", "Workbench.Host.dll", "Workbench.Host.deps.json", "Workbench.Host.runtimeconfig.json" })
            await File.WriteAllTextAsync(Path.Combine(_root, name), "synthetic " + name);
        var elf = new byte[64];
        new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1 }.CopyTo(elf, 0);
        elf[18] = 62;
        await File.WriteAllBytesAsync(Path.Combine(_root, "Workbench.Host"), elf);
        File.Copy(typeof(Wire).Assembly.Location, Path.Combine(_root, "Workbench.Protocol.dll"), overwrite: true);
        await File.WriteAllTextAsync(Path.Combine(_root, "publish-info.json"), "{\"runtime\":\"linux-x64\",\"selfContained\":true,\"hostExecutable\":\"Workbench.Host\",\"formatVersion\":1}");
        var hashes = new List<string>();
        string host = "";
        foreach (var path in Directory.EnumerateFiles(_root).Where(p => Path.GetFileName(p) != "SHA256SUMS"))
        {
            var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));
            hashes.Add(hash + "  " + Path.GetFileName(path));
            if (Path.GetFileName(path) == "Workbench.Host.dll") host = hash;
        }
        await File.WriteAllLinesAsync(Path.Combine(_root, "SHA256SUMS"), hashes);
        return host;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
