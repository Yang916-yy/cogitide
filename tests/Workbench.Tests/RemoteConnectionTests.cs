using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Workbench.Desktop;
using Workbench.Desktop.Remote;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class RemoteConnectionTests
{
    [Fact]
    public void SshUsesSystemConfigurationStrictHostKeysAndLiteralPosixArguments()
    {
        var profile = Ssh() with
        {
            HostPath = "/srv/Cogitide user's files/Workbench.Host.dll",
            DotnetPath = "/opt/dot net/dotnet",
            DataDirectory = "/tmp/data 'quoted'; $(touch should-never-run)"
        };
        var start = RemoteHostLaunch.CreateStartInfo(profile);
        Assert.Equal("ssh", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput && start.RedirectStandardError);
        Assert.Contains("BatchMode=yes", start.ArgumentList);
        Assert.Contains("StrictHostKeyChecking=yes", start.ArgumentList);
        Assert.Contains("ForwardAgent=no", start.ArgumentList);
        Assert.Contains("ClearAllForwardings=yes", start.ArgumentList);
        Assert.Contains("PermitLocalCommand=no", start.ArgumentList);
        Assert.Contains("RemoteCommand=none", start.ArgumentList);
        Assert.Contains("-x", start.ArgumentList);
        Assert.Contains("StdinNull=no", start.ArgumentList);
        Assert.Contains("ForkAfterAuthentication=no", start.ArgumentList);
        Assert.Equal("--", start.ArgumentList[^3]);
        Assert.Equal("fixture-host", start.ArgumentList[^2]);
        Assert.Equal("exec '/opt/dot net/dotnet' '/srv/Cogitide user'\"'\"'s files/Workbench.Host.dll' '--stdio' '--data-dir' '/tmp/data '\"'\"'quoted'\"'\"'; $(touch should-never-run)'", start.ArgumentList[^1]);
        Assert.DoesNotContain(start.ArgumentList, argument => argument.Contains("UserKnownHostsFile", StringComparison.Ordinal) || argument == "-A");
    }

    [Fact]
    public void WslExecUsesArgumentListWithoutShellOrLocalPathNormalization()
    {
        var profile = Ssh() with { Kind = HostConnectionKind.Wsl, Destination = "Ubuntu test", HostPath = "/home/user/a 'quoted' Host.dll", DataDirectory = "/home/user/data directory" };
        var start = RemoteHostLaunch.CreateStartInfo(profile);
        Assert.Equal("wsl.exe", start.FileName);
        Assert.Equal(new[] { "--distribution", "Ubuntu test", "--exec", "dotnet", "/home/user/a 'quoted' Host.dll", "--stdio", "--data-dir", "/home/user/data directory" }, start.ArgumentList);
        Assert.DoesNotContain("sh", start.ArgumentList);
        Assert.DoesNotContain("--login", start.ArgumentList);
    }

    [Fact]
    public void RemoteLaunchRemovesProviderAndCustomKeysButPreservesSshAuthenticationAndOtherWslEntries()
    {
        var start = RemoteHostLaunch.CreateStartInfo(Ssh());
        // Use synthetic names/values only. No environment values are displayed or changed globally.
        start.Environment.Clear();
        start.Environment["OPENAI_API_KEY"] = "synthetic-provider-key";
        start.Environment["custom_api_key"] = "synthetic-compatible-key";
        start.Environment["API_KEY"] = "synthetic-generic-key";
        start.Environment["MY_SELECTED_PROVIDER_TOKEN"] = "synthetic-custom-key";
        start.Environment["RECENT_PROVIDER_TOKEN"] = "synthetic-recent-key";
        start.Environment["SSH_AUTH_SOCK"] = "/fixture/agent.sock";
        start.Environment["PATH"] = "/fixture/bin";
        start.Environment["WSLENV"] = "PATH/l:OPENAI_API_KEY/u:MY_SELECTED_PROVIDER_TOKEN:RECENT_PROVIDER_TOKEN/p:custom_api_key:UNCHANGED/p:SSH_AUTH_SOCK";
        RemoteHostLaunch.SanitizeEnvironment(start, ["MY_SELECTED_PROVIDER_TOKEN", "recent_provider_token"]);
        foreach (var name in new[] { "OPENAI_API_KEY", "custom_api_key", "API_KEY", "MY_SELECTED_PROVIDER_TOKEN", "RECENT_PROVIDER_TOKEN" })
            Assert.False(start.Environment.ContainsKey(name));
        Assert.Equal("/fixture/agent.sock", start.Environment["SSH_AUTH_SOCK"]);
        Assert.Equal("/fixture/bin", start.Environment["PATH"]);
        Assert.Equal("PATH/l:UNCHANGED/p:SSH_AUTH_SOCK", start.Environment["WSLENV"]);
    }

    [Fact]
    public void RemoteLaunchRemovesAllSecretWslForwardingEntriesEvenWhenTheirSourceIsAbsent()
    {
        var start = RemoteHostLaunch.CreateStartInfo(Ssh() with { Kind = HostConnectionKind.Wsl, Destination = "Fixture distro" });
        start.Environment.Clear();
        start.Environment["WSLENV"] = "OPENAI_API_KEY/up:CUSTOM_PROVIDER_TOKEN/l";
        RemoteHostLaunch.SanitizeEnvironment(start, ["CUSTOM_PROVIDER_TOKEN"]);
        Assert.False(start.Environment.ContainsKey("WSLENV"));
    }

    [Fact]
    public async Task HostClientFiltersRemoteProcessEnvironmentBeforeStartingTheOwnedChild()
    {
        using var fixture = new FakeTransport("success");
        ProcessStartInfo? observed = null;
        await using var client = new HostClient(profile =>
        {
            observed = fixture.StartInfo(profile);
            observed.Environment["COGITIDE_ENV_FIXTURE_API_KEY"] = "synthetic-provider-key";
            observed.Environment["COGITIDE_ENV_FIXTURE_SELECTED"] = "synthetic-selected-key";
            observed.Environment["SSH_AUTH_SOCK"] = "/fixture/agent.sock";
            observed.Environment["WSLENV"] = "COGITIDE_ENV_FIXTURE_API_KEY/u:COGITIDE_ENV_FIXTURE_SELECTED:UNCHANGED/p";
            return observed;
        }) { SecretEnvironmentVariables = ["COGITIDE_ENV_FIXTURE_SELECTED"] };
        await client.StartAsync(Ssh());
        Assert.NotNull(observed);
        Assert.False(observed.Environment.ContainsKey("COGITIDE_ENV_FIXTURE_API_KEY"));
        Assert.False(observed.Environment.ContainsKey("COGITIDE_ENV_FIXTURE_SELECTED"));
        Assert.Equal("UNCHANGED/p", observed.Environment["WSLENV"]);
        Assert.Equal("/fixture/agent.sock", observed.Environment["SSH_AUTH_SOCK"]);
        Assert.True(client.Connected);
    }

    [Theory]
    [InlineData("-oProxyCommand=bad")]
    [InlineData("host -o StrictHostKeyChecking=no")]
    [InlineData("ssh://user@host")]
    [InlineData("host\nother")]
    [InlineData("host;touch /tmp/bad")]
    public void SshDestinationRejectsOptionsUrlsAndShellTokens(string destination) =>
        Assert.Throws<ArgumentException>(() => RemoteHostLaunch.CreateStartInfo(Ssh() with { Destination = destination }));

    [Theory]
    [InlineData("relative/host")]
    [InlineData("C:\\host.exe")]
    [InlineData("~/host")]
    [InlineData("/tmp/host\nother")]
    public void RemoteExecutableMustUseAnAbsolutePosixPath(string hostPath) =>
        Assert.Throws<ArgumentException>(() => RemoteHostLaunch.CreateStartInfo(Ssh() with { HostPath = hostPath }));

    [Fact]
    public void RemoteProfileSettingsRoundTripWithoutCredentialsAndRejectCorruptProfiles()
    {
        var profile = Ssh() with { HostPath = "/opt/Host.dll", DataDirectory = "/home/remote/data" };
        var settings = new DesktopSettings { LastWorkspace = "C:\\local-workspace" }.RememberConnection(profile);
        var json = JsonSerializer.Serialize(settings, Wire.Json);
        var restored = JsonSerializer.Deserialize<DesktopSettings>(json, Wire.Json)!.NormalizeBackend();
        Assert.Equal(profile, restored.ActiveConnection);
        Assert.Equal(new RunOptions(), restored.Run);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privateKey", json, StringComparison.OrdinalIgnoreCase);
        var corrupt = (restored with { ConnectionProfiles = [profile with { Destination = "-bad" }], Run = null! }).NormalizeBackend();
        Assert.Empty(corrupt.ConnectionProfiles);
        Assert.Equal(HostConnectionProfile.Local, corrupt.ActiveConnection);
        Assert.NotNull(corrupt.Run);
    }

    [Fact]
    public void WslDiscoveryDecodesUtf16NulAndBoundsTheList()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Ubuntu\r\nDebian\r\nUbuntu\r\n")).ToArray();
        Assert.Equal(new[] { "Ubuntu", "Debian" }, WslDistributions.Parse(utf16));
        Assert.Equal(new[] { "中文发行版" }, WslDistributions.Parse(Encoding.Unicode.GetBytes("中文发行版\r\n")));
        Assert.Equal(new[] { "Ubuntu", "Debian" }, WslDistributions.Parse(Encoding.UTF8.GetBytes("U\0b\0u\0n\0t\0u\0\n\0D\0e\0b\0i\0a\0n\0\n\0")));
        Assert.Throws<InvalidDataException>(() => WslDistributions.Parse(new byte[65_537]));
        Assert.Throws<InvalidDataException>(() => WslDistributions.Parse(Encoding.UTF8.GetBytes("--bad\n")));
    }

    [Fact]
    public async Task FakeWslDiscoveryExecutableReturnsUtf16DistributionNames()
    {
        using var fixture = new FakeTransport("wsl-discovery");
        Assert.Equal(new[] { "Ubuntu", "Debian" }, await WslDistributions.ListAsync(fixture.StartInfo(Ssh()), CancellationToken.None));
    }

    [Theory]
    [InlineData("Ssh")]
    [InlineData("Wsl")]
    public async Task FakeTransportInitializesRequestsAndClosesOwnedHostOnEof(string connectionKind)
    {
        var kind = Enum.Parse<HostConnectionKind>(connectionKind);
        using var fixture = new FakeTransport("success");
        var profile = Ssh() with { Kind = kind, Destination = kind == HostConnectionKind.Ssh ? "fixture-host" : "Fixture distro" };
        await using var client = new HostClient(fixture.StartInfo);
        await client.StartAsync(profile);
        Assert.True(client.Connected && client.IsRemote);
        Assert.Equal(profile, client.Connection);
        var response = await client.RequestAsync<JsonElement>("fixture.echo", new { text = "no paid model calls" });
        Assert.True(response.GetProperty("ok").GetBoolean());
        var launchArguments = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(fixture.ArgumentsPath))!;
        Assert.Contains(kind == HostConnectionKind.Ssh ? "BatchMode=yes" : "--exec", launchArguments);
        await client.DisposeAsync();
        Assert.Equal("eof", await fixture.ReadEofMarkerAsync());
        Assert.False(client.Connected);
    }

    [Theory]
    [InlineData("wrong-version", "protocol does not match")]
    [InlineData("missing-version", "protocol does not match")]
    [InlineData("old-capabilities", "missing required capabilities")]
    [InlineData("malformed", "valid Host protocol")]
    [InlineData("auth-failure", "authentication failed")]
    [InlineData("changed-key", "host key changed")]
    [InlineData("missing-host", "path does not exist")]
    public async Task StartupFailureIsClearRedactedAndDisposesTheTransport(string mode, string expected)
    {
        using var fixture = new FakeTransport(mode);
        await using var client = new HostClient(fixture.StartInfo);
        var exception = await Assert.ThrowsAsync<IOException>(() => client.StartAsync(Ssh()));
        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET_FIXTURE_DO_NOT_SHOW", exception.Message, StringComparison.Ordinal);
        Assert.False(client.Connected);
    }

    [Fact]
    public async Task DisconnectDoesNotReplayAnUnobservedPendingRequest()
    {
        using var fixture = new FakeTransport("pending");
        await using var client = new HostClient(fixture.StartInfo);
        await client.StartAsync(Ssh());
        var pending = client.RequestAsync<JsonElement>("fixture.pending");
        await client.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        Assert.Equal("eof", await fixture.ReadEofMarkerAsync());
        Assert.False(client.Connected);
    }

    [Fact]
    public async Task DisconnectWaitsForAnInflightPipeWriteBeforeSendingEof()
    {
        using var fixture = new FakeTransport("pending-large");
        await using var client = new HostClient(fixture.StartInfo);
        await client.StartAsync(Ssh());
        // The fixture briefly pauses after initialize, then drains without replying.
        // A payload larger than the OS pipe buffer keeps the write active at disconnect.
        var pending = client.RequestAsync<JsonElement>("fixture.pending", new { text = new string('x', 4 * 1024 * 1024) });
        await client.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        Assert.Equal("eof", await fixture.ReadEofMarkerAsync());
        Assert.False(client.Connected);
    }

    private static HostConnectionProfile Ssh() => new() { Id = "fixture", Kind = HostConnectionKind.Ssh, Destination = "fixture-host", HostPath = "/opt/cogitide/Workbench.Host" };

    private sealed class FakeTransport : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "cogitide-remote-test-" + Guid.NewGuid().ToString("N"));
        private readonly string _mode;
        public string EofPath => Path.Combine(_directory, "eof");
        public string ArgumentsPath => Path.Combine(_directory, "arguments.json");
        private string ScriptPath => Path.Combine(_directory, OperatingSystem.IsWindows() ? "fake-transport.ps1" : "fake-transport.sh");

        public FakeTransport(string mode)
        {
            _mode = mode;
            Directory.CreateDirectory(_directory);
            File.WriteAllText(ScriptPath, OperatingSystem.IsWindows() ? WindowsScript : PosixScript, new UTF8Encoding(false));
        }

        public ProcessStartInfo StartInfo(HostConnectionProfile profile)
        {
            var start = RemoteHostLaunch.CreateStartInfo(profile);
            var launchArguments = start.ArgumentList.ToArray();
            start.FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
            start.ArgumentList.Clear();
            if (OperatingSystem.IsWindows())
                foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File" }) start.ArgumentList.Add(argument);
            start.ArgumentList.Add(ScriptPath);
            start.ArgumentList.Add(_mode);
            start.ArgumentList.Add(EofPath);
            start.ArgumentList.Add(ArgumentsPath);
            start.Environment["COGITIDE_FIXTURE_LAUNCH"] = JsonSerializer.Serialize(launchArguments);
            return start;
        }

        public async Task<string?> ReadEofMarkerAsync()
        {
            var elapsed = Stopwatch.StartNew();
            string? observed = null;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(3))
            {
                try
                {
                    observed = await File.ReadAllTextAsync(EofPath);
                    if (observed == "eof") return observed;
                }
                catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException ||
                    (exception.HResult & 0xffff) is 32 or 33) { }
                // File creation is not the EOF observation: require its complete payload.
                await Task.Delay(20);
            }
            return observed;
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);

        private const string WindowsScript = """
            param([string]$Mode, [string]$EofFile, [string]$ArgumentsFile)
            $ErrorActionPreference = 'Stop'
            [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
            [IO.File]::WriteAllText($ArgumentsFile, $env:COGITIDE_FIXTURE_LAUNCH)
            if ($Mode -eq 'wsl-discovery') {
                $bytes = [Text.Encoding]::Unicode.GetBytes("Ubuntu`r`nDebian`r`n")
                [Console]::OpenStandardOutput().Write($bytes, 0, $bytes.Length)
                exit 0
            }
            if ($Mode -eq 'auth-failure') { [Console]::Error.WriteLine('Permission denied (publickey). SECRET_FIXTURE_DO_NOT_SHOW'); exit 255 }
            if ($Mode -eq 'changed-key') { [Console]::Error.WriteLine('REMOTE HOST IDENTIFICATION HAS CHANGED! SECRET_FIXTURE_DO_NOT_SHOW'); exit 255 }
            if ($Mode -eq 'missing-host') { [Console]::Error.WriteLine('No such file or directory SECRET_FIXTURE_DO_NOT_SHOW'); exit 127 }
            $line = [Console]::ReadLine()
            if ($null -eq $line) { exit 1 }
            if ($Mode -eq 'malformed') { [Console]::WriteLine('not-json'); exit 1 }
            $version = if ($Mode -eq 'wrong-version') { 999 } else { 1 }
            $capabilities = if ($Mode -eq 'old-capabilities') { @() } else { @('session.view', 'run.continue') }
            $result = if ($Mode -eq 'missing-version') { @{} } else { @{ protocolVersion = $version; capabilities = $capabilities } }
            [Console]::WriteLine((@{ id = '1'; result = $result } | ConvertTo-Json -Compress -Depth 8))
            if ($Mode -eq 'pending-large') {
                Start-Sleep -Milliseconds 250
                $null = [Console]::In.ReadToEnd()
                [IO.File]::WriteAllText($EofFile, 'eof')
                exit 0
            }
            while ($null -ne [Console]::ReadLine()) {
                if ($Mode -ne 'pending') { [Console]::WriteLine('{"id":"2","result":{"ok":true}}') }
            }
            [IO.File]::WriteAllText($EofFile, 'eof')
            """;

        private const string PosixScript = """
            mode="$1"
            eof_file="$2"
            arguments_file="$3"
            printf '%s' "$COGITIDE_FIXTURE_LAUNCH" > "$arguments_file"
            case "$mode" in
              wsl-discovery) printf 'U\000b\000u\000n\000t\000u\000\r\000\n\000D\000e\000b\000i\000a\000n\000\r\000\n\000'; exit 0;;
              auth-failure) printf '%s\n' 'Permission denied (publickey). SECRET_FIXTURE_DO_NOT_SHOW' >&2; exit 255;;
              changed-key) printf '%s\n' 'REMOTE HOST IDENTIFICATION HAS CHANGED! SECRET_FIXTURE_DO_NOT_SHOW' >&2; exit 255;;
              missing-host) printf '%s\n' 'No such file or directory SECRET_FIXTURE_DO_NOT_SHOW' >&2; exit 127;;
            esac
            IFS= read -r line || exit 1
            case "$mode" in
              malformed) printf '%s\n' 'not-json'; exit 1;;
              wrong-version) printf '%s\n' '{"id":"1","result":{"protocolVersion":999,"capabilities":["session.view","run.continue"]}}';;
              missing-version) printf '%s\n' '{"id":"1","result":{}}';;
              old-capabilities) printf '%s\n' '{"id":"1","result":{"protocolVersion":1,"capabilities":[]}}';;
              *) printf '%s\n' '{"id":"1","result":{"protocolVersion":1,"capabilities":["session.view","run.continue"]}}';;
            esac
            if [ "$mode" = 'pending-large' ]; then
              sleep 0.25
              cat > /dev/null
              printf '%s' 'eof' > "$eof_file"
              exit 0
            fi
            while IFS= read -r line; do
              if [ "$mode" != 'pending' ]; then printf '%s\n' '{"id":"2","result":{"ok":true}}'; fi
            done
            printf '%s' 'eof' > "$eof_file"
            """;
    }
}
