using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Mxc.Sdk.V1;
using Workbench.Protocol;

namespace Workbench.Tools;

/// <summary>Authors SDK policies only. MXC owns platform detection, validation and native enforcement.</summary>
public static class OsIsolationCapabilities
{
    public static OsIsolationStatus Probe()
    {
        var backend = OperatingSystem.IsWindows() ? "ProcessContainer" : OperatingSystem.IsLinux() ? "Bubblewrap" : OperatingSystem.IsMacOS() ? "Seatbelt" : "unsupported";
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return new(false, backend, "Microsoft.Mxc.Sdk 1.0.1 does not include an osx-x64 native runtime asset; the pinned package includes osx-arm64 only.");
        try
        {
            var support = MxcPlatform.GetPlatformSupport();
            var expected = OperatingSystem.IsWindows() ? ContainmentBackend.ProcessContainer : OperatingSystem.IsLinux() ? ContainmentBackend.Bubblewrap : ContainmentBackend.Seatbelt;
            if (!support.IsSupported || backend == "unsupported" || !support.AvailableMethods.Contains(expected))
                return new(false, backend, support.Reason ?? "The required native process backend is unavailable.");
            if (OperatingSystem.IsWindows() && !MxcPlatform.GetAvailableBackends().Any(item => item.Backend == expected && item.Capabilities.Contains(BackendCapability.FilesystemDeniedPaths)))
                return new(false, backend, "Native ProcessContainer cannot enforce protected Host-directory denials.");
            return new(true, backend);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or MxcException or TypeInitializationException or PlatformNotSupportedException)
        { return new(false, backend, $"Native isolation is unavailable ({ex.GetType().Name})."); }
    }
}

public static class OsIsolationPolicy
{
    public static ContainerRequest Create(ProcessStartInfo worker, string workspace, string scratch, string protectedDataDirectory,
        ExecutionMode executionMode = ExecutionMode.WorkspaceWrite)
    {
        workspace = WorkspaceService.Root(workspace);
        scratch = WorkspaceService.Root(scratch);
        protectedDataDirectory = Path.GetFullPath(protectedDataDirectory);
        if (Within(workspace, protectedDataDirectory) || Within(protectedDataDirectory, workspace) || Within(workspace, scratch) || Within(scratch, workspace))
            throw new UnauthorizedAccessException("Workspace cannot overlap protected Host state or worker scratch.");
        if (!Path.IsPathFullyQualified(worker.FileName) || !File.Exists(worker.FileName) || !string.IsNullOrWhiteSpace(worker.Arguments))
            throw new ArgumentException("The isolated worker requires an absolute executable and ArgumentList; shell command strings are not accepted.");
        var files = new FilesystemPolicy();
        if (!Enum.IsDefined(executionMode)) throw new ArgumentOutOfRangeException(nameof(executionMode));
        if (executionMode == ExecutionMode.ReadOnly) files.ReadonlyPaths.Add(workspace);
        else files.ReadwritePaths.Add(workspace);
        files.ReadwritePaths.Add(scratch);
        AddExisting(files.ReadonlyPaths, Path.GetDirectoryName(worker.FileName)!);
        AddExisting(files.ReadonlyPaths, AppContext.BaseDirectory);
        // The current runtime installation is explicit; no whole-profile or account-directory grant.
        AddExisting(files.ReadonlyPaths, Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")));
        foreach (var argument in worker.ArgumentList)
            if (Path.IsPathFullyQualified(argument) && File.Exists(argument) && argument.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                AddExisting(files.ReadonlyPaths, Path.GetDirectoryName(argument)!);
        if (OperatingSystem.IsWindows())
        {
            AddExisting(files.ReadonlyPaths, Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            // Use installed Git without granting every application or the user's profile.
            foreach (var programFiles in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (!string.IsNullOrWhiteSpace(programFiles)) AddExisting(files.ReadonlyPaths, Path.Combine(programFiles, "Git"));
        }
        else
        {
            foreach (var path in new[] { "/usr", "/bin", "/sbin", "/lib", "/lib64", "/etc/ld.so.cache", "/etc/ld.so.conf", "/etc/localtime", "/etc/ssl/certs" }) AddExisting(files.ReadonlyPaths, path);
            if (OperatingSystem.IsMacOS())
                foreach (var path in new[] { "/System", "/Library/Apple", "/Library/Frameworks", "/opt/homebrew" }) AddExisting(files.ReadonlyPaths, path);
        }
        files.DeniedPaths.Add(protectedDataDirectory);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { ".ssh", ".aws", ".azure", ".codex", ".config/gcloud" })
            if (!string.IsNullOrWhiteSpace(home))
            {
                var path = Path.Combine(home, name.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(path)) files.DeniedPaths.Add(path);
            }
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HOME"] = scratch, ["USERPROFILE"] = scratch, ["APPDATA"] = scratch, ["LOCALAPPDATA"] = scratch,
            ["TEMP"] = scratch, ["TMP"] = scratch, ["TMPDIR"] = scratch,
            ["DOTNET_CLI_HOME"] = scratch, ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1", ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1"
        };
        if (OperatingSystem.IsWindows())
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            environment["SYSTEMROOT"] = windows;
            environment["WINDIR"] = windows;
            environment["COMSPEC"] = Path.Combine(windows, "System32", "cmd.exe");
            environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            environment["PATH"] = string.Join(Path.PathSeparator, new[] { Path.GetDirectoryName(worker.FileName)!, Path.Combine(windows, "System32"), Path.Combine(windows, "System32", "WindowsPowerShell", "v1.0"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd") });
        }
        else environment["PATH"] = "/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin";
        return new ContainerRequest(string.Join(" ", new[] { worker.FileName }.Concat(worker.ArgumentList).Select(QuoteArgument)))
        {
            Containment = OperatingSystem.IsWindows() ? new Containment.ProcessContainer
            {
                Ui = new ProcessContainerUiPolicy
                {
                    Isolation = ProcessContainerUiIsolation.Container, DesktopSystemControl = false,
                    SystemSettings = ProcessContainerSystemSettings.None, Ime = false
                }
            } : new Containment.Process(), Filesystem = files,
            Network = new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny },
                Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny }
            },
            // Windows CoreCLR imports user32: Win32k-disable prevents the DLL from loading.
            // Keep all Host UI restrictions explicitly denied while allowing that subsystem
            // inside the native container. Bubblewrap has no UI-policy primitive.
            Ui = OperatingSystem.IsLinux() ? null : new UiPolicy
            {
                Disable = !OperatingSystem.IsWindows(), Clipboard = ClipboardPolicy.None, AllowInputInjection = false
            }, Environment = environment, InheritDefaultEnvironment = false,
            WorkingDirectory = workspace, TimeoutMs = 604_800_000
        };
    }

    private static void AddExisting(List<string> paths, string path)
    {
        if ((Directory.Exists(path) || File.Exists(path)) && !paths.Contains(Path.GetFullPath(path), PathComparer)) paths.Add(Path.GetFullPath(path));
    }
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static bool Within(string path, string root) => PathComparer.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(root)) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // MXC accepts one native command line. BCL ProcessStartInfo owns the application's argv;
    // these are standard platform argv escaping rules, never shell interpolation.
    private static string QuoteArgument(string value)
    {
        if (value.Contains('\0') || value.Contains('\r') || value.Contains('\n')) throw new ArgumentException("Worker arguments cannot contain control characters.");
        if (!OperatingSystem.IsWindows()) return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var result = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}
