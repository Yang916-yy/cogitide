using System.Security.Cryptography;
using System.Text.Json;
using Workbench.Protocol;

namespace Workbench.Host.RemoteService;

/// <summary>Private service discovery only; session and event persistence stays in SqliteSessionStore.</summary>
internal sealed class RemoteServiceFiles
{
    internal const string DirectoryName = "remote-service";
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public string DirectoryPath { get; }
    public string EndpointPath => Path.Combine(DirectoryPath, "endpoint.json");
    public string LogPath => Path.Combine(DirectoryPath, "service.log");

    internal void PrepareLog()
    {
        if (File.Exists(LogPath)) { RequirePrivate(LogPath); return; }
        using var stream = new FileStream(LogPath, PrivateCreateOptions());
    }

    internal RemoteServiceFiles(string dataDirectory, bool create = false)
    {
        DirectoryPath = Path.Combine(Path.GetFullPath(dataDirectory), DirectoryName);
        if (create)
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(DirectoryPath);
            else Directory.CreateDirectory(DirectoryPath, PrivateDirectory);
        }
        RequirePrivate(DirectoryPath, directory: true);
    }

    internal string GetOrCreateServiceId()
    {
        var path = Path.Combine(DirectoryPath, "identity");
        if (!File.Exists(path))
        {
            try
            {
                using var stream = new FileStream(path, PrivateCreateOptions());
                using var writer = new StreamWriter(stream);
                writer.Write(Guid.NewGuid().ToString("N"));
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        return ReadServiceId();
    }

    private string ReadServiceId()
    {
        var path = Path.Combine(DirectoryPath, "identity");
        RequirePrivate(path);
        if (new FileInfo(path).Length > 128) throw new InvalidDataException("The service identity file is not recognized; it was not replaced.");
        var identity = File.ReadAllText(path).Trim();
        if (!Guid.TryParseExact(identity, "N", out _)) throw new InvalidDataException("The service identity file is not recognized; it was not replaced.");
        return identity;
    }

    internal async Task<RemoteServiceEndpoint> ReadAsync(string? expectedServiceId = null, CancellationToken ct = default)
    {
        RequirePrivate(EndpointPath);
        var info = new FileInfo(EndpointPath);
        if (info.Length > 16_384) throw new InvalidDataException("The service endpoint descriptor is too large.");
        var endpoint = JsonSerializer.Deserialize<RemoteServiceEndpoint>(await File.ReadAllTextAsync(EndpointPath, ct), Wire.Json)
            ?? throw new InvalidDataException("The service endpoint descriptor is empty.");
        Validate(endpoint, expectedServiceId);
        if (endpoint.Identity.ServiceId != ReadServiceId()) throw new InvalidDataException("The service descriptor does not match its installed identity.");
        return endpoint;
    }

    internal async Task WriteAsync(RemoteServiceEndpoint endpoint, CancellationToken ct = default)
    {
        Validate(endpoint, null);
        // A stale descriptor belongs to this service identity. Never replace an unrecognized file.
        if (File.Exists(EndpointPath)) await ReadAsync(endpoint.Identity.ServiceId, ct);
        var temporary = Path.Combine(DirectoryPath, ".endpoint-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var stream = new FileStream(temporary, PrivateCreateOptions()))
            {
                await JsonSerializer.SerializeAsync(stream, endpoint, Wire.Json, ct);
                await stream.FlushAsync(ct);
            }
            File.Move(temporary, EndpointPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal async Task RemoveOwnEndpointAsync(string instanceId)
    {
        try
        {
            if (File.Exists(EndpointPath) && (await ReadAsync()).Identity.InstanceId == instanceId) File.Delete(EndpointPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
    }

    internal static void Validate(RemoteServiceEndpoint endpoint, string? expectedServiceId)
    {
        if (endpoint.Identity is null || !Guid.TryParseExact(endpoint.Identity.ServiceId, "N", out _) || !Guid.TryParseExact(endpoint.Identity.InstanceId, "N", out _) ||
            endpoint.Identity.ProtocolVersion != Wire.Version || endpoint.Token is not { Length: 64 } || !endpoint.Token.All(Uri.IsHexDigit) ||
            !Uri.TryCreate(endpoint.Address, UriKind.Absolute, out var address) || address.Scheme != "http" || address.UserInfo.Length != 0 ||
            address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0 ||
            !System.Net.IPAddress.TryParse(address.Host, out var host) || !System.Net.IPAddress.IsLoopback(host) || address.Port <= 0)
            throw new InvalidDataException("The service endpoint must be a matching authenticated loopback endpoint.");
        if (expectedServiceId is not null && endpoint.Identity.ServiceId != expectedServiceId)
            throw new InvalidDataException("The remote service identity changed. Reconfigure the connection after verifying the endpoint.");
    }

    internal static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private static FileStreamOptions PrivateCreateOptions()
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = PrivateFile;
        return options;
    }

    private static void RequirePrivate(string path, bool directory = false)
    {
        FileSystemInfo entry = directory ? new DirectoryInfo(path) : new FileInfo(path);
        if (entry.LinkTarget is not null)
            throw new InvalidDataException("Service discovery paths cannot be symbolic links.");
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new UnauthorizedAccessException("Service discovery must be readable only by its owner.");
    }
}
