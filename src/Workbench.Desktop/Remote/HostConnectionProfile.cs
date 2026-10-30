using System.Text.Json.Serialization;

namespace Workbench.Desktop.Remote;

internal enum HostConnectionKind { Local, Wsl, Ssh }

/// <summary>Nonsecret launch preferences. Credentials stay in OpenSSH and on the selected Host.</summary>
internal sealed record HostConnectionProfile
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "";
    public HostConnectionKind Kind { get; init; }
    public string Destination { get; init; } = "";
    public string HostPath { get; init; } = "/opt/cogitide/Workbench.Host";
    public string DotnetPath { get; init; } = "dotnet";
    public string? DataDirectory { get; init; }

    public static HostConnectionProfile Local { get; } = new() { Id = "local", Name = "Local", Kind = HostConnectionKind.Local };
    [JsonIgnore] public bool IsRemote => Kind != HostConnectionKind.Local;
    [JsonIgnore] public string DisplayLabel => Kind switch
    {
        HostConnectionKind.Local => Localization.Text("RemoteLocal"),
        HostConnectionKind.Wsl => $"WSL · {(string.IsNullOrWhiteSpace(Name) ? Destination : Name)}",
        _ => $"SSH · {(string.IsNullOrWhiteSpace(Name) ? Destination : Name)}"
    };

    public override string ToString() => DisplayLabel;

    public void Validate()
    {
        if (!Enum.IsDefined(Kind)) throw new ArgumentException("Choose a supported connection type.");
        ValidateText(Id, "Profile identifier", 80, required: true);
        ValidateText(Name, "Profile name", 128);
        if (Kind == HostConnectionKind.Local)
        {
            if (Id != Local.Id) throw new ArgumentException("The local connection has a fixed identifier.");
            return;
        }
        if (Id == Local.Id) throw new ArgumentException("A remote profile cannot use the local connection identifier.");
        ValidateText(Destination, Kind == HostConnectionKind.Wsl ? "WSL distribution" : "SSH destination", 255, required: true);
        if (Destination.StartsWith('-')) throw new ArgumentException("The destination cannot start with an option prefix.");
        if (Kind == HostConnectionKind.Ssh && Destination.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_' or '@' or ':' or '[' or ']')))
            throw new ArgumentException("Use a hostname, user@hostname or an SSH configuration alias, without options or a URL.");
        RemotePaths.RequireAbsolute(HostPath, "Remote Host path");
        ValidateText(DotnetPath, "Remote dotnet path", 4096, required: true);
        if (!RemotePaths.IsAbsolute(DotnetPath) &&
            (DotnetPath.StartsWith('-') || DotnetPath.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-' or '_'))))
            throw new ArgumentException("Use an absolute POSIX dotnet path or a single executable name such as dotnet.");
        if (!string.IsNullOrEmpty(DataDirectory)) RemotePaths.RequireAbsolute(DataDirectory, "Remote data directory");
    }

    private static void ValidateText(string? value, string label, int limit, bool required = false)
    {
        if (value is null || value.Length > limit || value.Any(char.IsControl) || (required && string.IsNullOrWhiteSpace(value)))
            throw new ArgumentException($"{label} must {(required ? "be provided and " : "")}contain at most {limit} characters without control characters.");
    }
}

internal static class RemotePaths
{
    public static bool IsAbsolute(string? path) => path is { Length: > 0 and <= 4096 } && path[0] == '/' && !path.Any(char.IsControl);

    public static void RequireAbsolute(string? path, string label = "Remote workspace path")
    {
        if (!IsAbsolute(path)) throw new ArgumentException($"{label} must be an absolute POSIX path beginning with /, without control characters.");
    }
}
