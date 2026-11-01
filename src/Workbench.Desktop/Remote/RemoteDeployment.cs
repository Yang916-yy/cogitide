using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Protocol;
using L = Workbench.Desktop.Localization;

namespace Workbench.Desktop.Remote;

internal sealed record RemoteDeploymentRequest(HostConnectionProfile Connection, string PackageDirectory,
    string InstallationDirectory, string DataDirectory, IReadOnlyList<string>? SecretEnvironmentVariables = null);

/// <summary>Explicit user deployment using existing publish manifests and native OpenSSH/WSL tools.</summary>
internal static class RemoteDeployment
{
    internal static async Task<HostConnectionProfile> DeployAsync(RemoteDeploymentRequest request, IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var profile = request.Connection with { DurableService = false, ServiceId = null };
        profile.Validate();
        RemotePaths.RequireAbsolute(request.InstallationDirectory, "New installation directory");
        RemotePaths.RequireAbsolute(request.DataDirectory, "New service data directory");
        if (request.InstallationDirectory.TrimEnd('/') is "" || request.DataDirectory.TrimEnd('/') is "" ||
            request.InstallationDirectory.TrimEnd('/') == request.DataDirectory.TrimEnd('/'))
            throw new ArgumentException("Choose separate new installation and data directories under your remote account.");
        progress?.Report(L.Text("RemoteServiceVerifyPackage"));
        var package = await RemoteDeploymentPackage.ValidateAsync(request.PackageDirectory, await RemoteDeploymentPackage.MatchingHostHashAsync(ct), ct);
        progress?.Report(L.Text("RemoteServiceCheckArchitecture"));
        var platform = await ExecuteAsync(Command(profile, ["/bin/sh", "-c", "uname -s; uname -m"], request.SecretEnvironmentVariables), TimeSpan.FromSeconds(20), ct);
        if (ParseRuntime(platform) != package.Runtime) throw new InvalidDataException("The prebuilt package RID does not match the selected endpoint.");
        var install = request.InstallationDirectory.TrimEnd('/');
        var data = request.DataDirectory.TrimEnd('/');
        progress?.Report(L.Text("RemoteServiceCreateDirectories"));
        // No replacement, upgrade, config rewrite or cleanup of an existing remote path is authorized here.
        await ExecuteAsync(Command(profile, ["/bin/sh", "-c", "umask 077; test ! -e " + Quote(install) + " && test ! -L " + Quote(install) +
            " && test ! -e " + Quote(data) + " && test ! -L " + Quote(data) + " && mkdir -- " + Quote(install) + " && mkdir -- " + Quote(data)], request.SecretEnvironmentVariables), TimeSpan.FromSeconds(20), ct);
        var temporary = Path.Combine(Path.GetTempPath(), "cogitide-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var archive = Path.Combine(temporary, "package.tar");
        try
        {
            progress?.Report(L.Text("RemoteServiceTransferAssets"));
            await TarFile.CreateFromDirectoryAsync(package.DirectoryPath, archive, includeBaseDirectory: false, ct);
            await using var archiveInput = File.OpenRead(archive);
            var archiveHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(archiveInput, ct));
            var remoteArchive = install + "/package.tar";
            if (profile.Kind == HostConnectionKind.Ssh)
                await ExecuteAsync(Scp(profile, archive, remoteArchive, request.SecretEnvironmentVariables), TimeSpan.FromMinutes(5), ct);
            else
            {
                var mounted = (await ExecuteAsync(Command(profile, ["wslpath", "-a", "-u", archive], request.SecretEnvironmentVariables), TimeSpan.FromSeconds(20), ct)).Trim();
                RemotePaths.RequireAbsolute(mounted, "WSL transfer source");
                await ExecuteAsync(Command(profile, ["cp", "--", mounted, remoteArchive], request.SecretEnvironmentVariables), TimeSpan.FromMinutes(2), ct);
            }
            progress?.Report(L.Text("RemoteServiceVerifyTransfer"));
            var verify = "set -eu; cd -- " + Quote(install) + "; " +
                "if command -v sha256sum >/dev/null 2>&1; then check='sha256sum'; else check='shasum -a 256'; fi; " +
                "printf '%s  package.tar\\n' " + Quote(archiveHash) + " | $check -c - >/dev/null; " +
                "tar -xf package.tar; $check -c SHA256SUMS >/dev/null; chmod u+x Workbench.Host; rm -- package.tar";
            await ExecuteAsync(Command(profile, ["/bin/sh", "-c", verify], request.SecretEnvironmentVariables), TimeSpan.FromMinutes(2), ct);
            profile = profile with { HostPath = install + "/Workbench.Host", DataDirectory = data };
            progress?.Report(L.Text("RemoteServiceStartDetached"));
            return await StartAsync(profile, ct, request.SecretEnvironmentVariables);
        }
        finally
        {
            // Only this newly generated local temporary archive directory is removed.
            if (File.Exists(archive)) File.Delete(archive);
            Directory.Delete(temporary);
        }
    }

    internal static async Task<HostConnectionProfile> StartAsync(HostConnectionProfile profile, CancellationToken ct = default, IReadOnlyList<string>? secretEnvironmentVariables = null)
    {
        profile.Validate();
        RemotePaths.RequireAbsolute(profile.DataDirectory, "Detached service data directory");
        var arguments = ExecutableArguments(profile).Concat(["--service-start", "--data-dir", profile.DataDirectory!]).ToList();
        if (profile.ServiceId is { Length: > 0 }) arguments.AddRange(["--expected-service-id", profile.ServiceId]);
        var output = await ExecuteAsync(Command(profile, arguments, secretEnvironmentVariables), TimeSpan.FromSeconds(30), ct);
        var identity = JsonSerializer.Deserialize<RemoteServiceIdentity>(output.Trim(), Wire.Json)
            ?? throw new InvalidDataException("The started service did not return its identity.");
        if (!Guid.TryParseExact(identity.ServiceId, "N", out _) || identity.ProtocolVersion != Wire.Version ||
            (profile.ServiceId is { Length: > 0 } expected && identity.ServiceId != expected))
            throw new InvalidDataException("The started service identity/protocol is incompatible with this connection.");
        return profile with { DurableService = true, ServiceId = identity.ServiceId };
    }

    internal static async Task StopAsync(HostConnectionProfile profile, CancellationToken ct = default, IReadOnlyList<string>? secretEnvironmentVariables = null)
    {
        profile.Validate();
        if (!profile.DurableService) throw new ArgumentException("Select a verified detached service profile.");
        var arguments = ExecutableArguments(profile).Concat(["--service-stop", "--data-dir", profile.DataDirectory!, "--expected-service-id", profile.ServiceId!]).ToArray();
        await ExecuteAsync(Command(profile, arguments, secretEnvironmentVariables), TimeSpan.FromSeconds(30), ct);
    }

    internal static string ParseRuntime(string output)
    {
        var values = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length != 2) throw new InvalidDataException("The endpoint returned an unexpected architecture response.");
        var platform = values[0] switch { "Linux" => "linux", "Darwin" => "osx", _ => throw new PlatformNotSupportedException("Guided deployment requires a Linux or macOS endpoint.") };
        var architecture = values[1] switch { "x86_64" or "amd64" => "x64", "aarch64" or "arm64" => "arm64", _ => throw new PlatformNotSupportedException("The endpoint architecture is not supported.") };
        return platform + "-" + architecture;
    }

    private static IReadOnlyList<string> ExecutableArguments(HostConnectionProfile profile) =>
        profile.HostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? [profile.DotnetPath, profile.HostPath] : [profile.HostPath];

    internal static ProcessStartInfo Command(HostConnectionProfile profile, IReadOnlyList<string> arguments, IReadOnlyList<string>? secretEnvironmentVariables = null)
    {
        var start = RemoteHostLaunch.CreateCommandStartInfo(profile, arguments);
        RemoteHostLaunch.SanitizeEnvironment(start, secretEnvironmentVariables);
        return start;
    }

    internal static ProcessStartInfo Scp(HostConnectionProfile profile, string archive, string remoteArchive, IReadOnlyList<string>? secretEnvironmentVariables = null)
    {
        profile.Validate();
        if (profile.Kind != HostConnectionKind.Ssh) throw new ArgumentException("SCP requires SSH.");
        RemotePaths.RequireAbsolute(remoteArchive);
        var start = new ProcessStartInfo("scp") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in new[] { "-B", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ForwardAgent=no", "-o", "ClearAllForwardings=yes", "-o", "PermitLocalCommand=no", "--", Path.GetFullPath(archive), profile.Destination + ":" + remoteArchive }) start.ArgumentList.Add(argument);
        RemoteHostLaunch.SanitizeEnvironment(start, secretEnvironmentVariables);
        return start;
    }

    private static string Quote(string value) => RemoteHostLaunch.QuotePosixArgument(value);

    private static async Task<string> ExecuteAsync(ProcessStartInfo start, TimeSpan timeout, CancellationToken ct)
    {
        using var process = Process.Start(start) ?? throw new IOException("The system SSH/WSL deployment command did not start.");
        process.StandardInput.Close();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var output = ReadBoundedAsync(process.StandardOutput, 1_048_576, deadline.Token);
        var errors = ReadBoundedAsync(process.StandardError, 16_384, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            var stdout = await output;
            var stderr = await errors;
            if (process.ExitCode != 0)
            {
                var reason = stderr.Contains("HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase) || stderr.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase)
                    ? "SSH host-key verification failed. Verify the fingerprint through a trusted channel outside Cogitide."
                    : stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ? "SSH authentication or remote directory permissions were denied."
                    : "The remote deployment command failed. Existing paths are not replaced; inspect the endpoint-local installation/service log.";
                throw new IOException(reason);
            }
            return stdout;
        }
        catch
        {
            ProcessShutdown.Terminate(process);
            try { await Task.WhenAll(output, errors); } catch (Exception e) when (e is OperationCanceledException or IOException) { }
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) is var count && count > 0)
            if (result.Length < limit) result.Append(buffer, 0, Math.Min(count, limit - result.Length));
        return result.ToString();
    }
}
