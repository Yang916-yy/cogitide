using System.Diagnostics;
using System.Text;

namespace Workbench.Desktop.Remote;

/// <summary>Adapts existing stdio Host arguments to the system OpenSSH or WSL process.</summary>
internal static class RemoteHostLaunch
{
    public static ProcessStartInfo CreateStartInfo(HostConnectionProfile profile)
        => CreateCommandStartInfo(profile, HostArguments(profile));

    internal static ProcessStartInfo CreateCommandStartInfo(HostConnectionProfile profile, IReadOnlyList<string> hostArguments)
    {
        profile.Validate();
        if (!profile.IsRemote) throw new ArgumentException("Use the existing local Host locator for a local connection.", nameof(profile));
        var start = new ProcessStartInfo
        {
            FileName = profile.Kind == HostConnectionKind.Ssh ? "ssh" : "wsl.exe",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        if (profile.Kind == HostConnectionKind.Ssh)
        {
            foreach (var argument in new[] { "-T", "-x", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ForwardAgent=no", "-o", "ClearAllForwardings=yes", "-o", "PermitLocalCommand=no", "-o", "RemoteCommand=none", "-o", "StdinNull=no", "-o", "ForkAfterAuthentication=no", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=2", "--", profile.Destination })
                start.ArgumentList.Add(argument);
            // OpenSSH sends a command string to the user's POSIX login shell. Quote each data argument
            // independently; exec makes the owned channel the Host's stdin/EOF lifetime.
            start.ArgumentList.Add("exec " + string.Join(' ', hostArguments.Select(QuotePosixArgument)));
        }
        else
        {
            start.ArgumentList.Add("--distribution");
            start.ArgumentList.Add(profile.Destination);
            start.ArgumentList.Add("--exec");
            foreach (var argument in hostArguments) start.ArgumentList.Add(argument);
        }
        return start;
    }

    internal static IReadOnlyList<string> HostArguments(HostConnectionProfile profile)
    {
        var arguments = new List<string>();
        if (profile.HostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) arguments.Add(profile.DotnetPath);
        arguments.Add(profile.HostPath);
        arguments.Add(profile.DurableService ? "--service-bridge" : "--stdio");
        if (profile.DurableService)
        {
            arguments.Add("--expected-service-id");
            arguments.Add(profile.ServiceId!);
        }
        if (!string.IsNullOrEmpty(profile.DataDirectory))
        {
            arguments.Add("--data-dir");
            arguments.Add(profile.DataDirectory);
        }
        return arguments;
    }

    internal static string QuotePosixArgument(string argument) => "'" + argument.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    internal static void SanitizeEnvironment(ProcessStartInfo start, IEnumerable<string>? secretEnvironmentVariables)
    {
        var explicitNames = new HashSet<string>((secretEnvironmentVariables ?? []).Where(name => !string.IsNullOrWhiteSpace(name)), StringComparer.OrdinalIgnoreCase);
        bool IsSecret(string name) => !name.Equals("SSH_AUTH_SOCK", StringComparison.OrdinalIgnoreCase) &&
            (name.Equals("API_KEY", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) || explicitNames.Contains(name));
        foreach (var name in start.Environment.Keys.Where(IsSecret).ToArray()) start.Environment.Remove(name);
        if (!start.Environment.TryGetValue("WSLENV", out var forwarded) || string.IsNullOrEmpty(forwarded)) return;
        var entries = forwarded.Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => !IsSecret(entry.Split('/', 2)[0])).ToArray();
        if (entries.Length == 0) start.Environment.Remove("WSLENV");
        else start.Environment["WSLENV"] = string.Join(':', entries);
    }
}
