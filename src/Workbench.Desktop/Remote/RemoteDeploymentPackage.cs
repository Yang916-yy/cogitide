using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text.Json;
using Workbench.Protocol;

namespace Workbench.Desktop.Remote;

/// <summary>Reads the existing publish.ps1 manifest; no download, SDK or dependency resolver is invoked.</summary>
internal sealed record RemoteDeploymentPackage(string DirectoryPath, string Runtime, string HostSha256,
    IReadOnlyDictionary<string, string> Files)
{
    internal static async Task<RemoteDeploymentPackage> ValidateAsync(string directory, string expectedHostSha256, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(directory);
        if (File.Exists(Path.Combine(root, ".publish-incomplete"))) throw new InvalidDataException("The selected publish has not completed.");
        if ((new DirectoryInfo(root).Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The publish directory cannot be a symbolic link.");
        var manifest = Path.Combine(root, "SHA256SUMS");
        if (new FileInfo(manifest).Length > 4 * 1024 * 1024) throw new InvalidDataException("The release hash manifest is too large.");
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in await File.ReadAllLinesAsync(manifest, ct))
        {
            if (line.Length < 67 || line[64..66] != "  " || !line[..64].All(Uri.IsHexDigit)) throw new InvalidDataException("The release hash manifest is invalid.");
            var relative = line[66..];
            if (relative.Length > 4096 || relative.Any(char.IsControl) || relative.Contains('\\') || relative.Split('/').Any(part => part is "" or "." or "..") ||
                Path.IsPathRooted(relative) || !entries.TryAdd(relative, line[..64].ToLowerInvariant())) throw new InvalidDataException("The release hash manifest contains an unsafe or repeated path.");
            var filename = Path.GetFileName(relative);
            if (relative.Split('/').Any(part => part.Equals("oauth", StringComparison.OrdinalIgnoreCase) || part.Equals("remote-service", StringComparison.OrdinalIgnoreCase)) ||
                filename.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || filename.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase) || filename.StartsWith("id_ed25519", StringComparison.OrdinalIgnoreCase) ||
                filename.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || filename.EndsWith(".credentials", StringComparison.OrdinalIgnoreCase) ||
                filename.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || filename.EndsWith(".key", StringComparison.OrdinalIgnoreCase) ||
                filename is "auth.json" or "settings.json" or "mcp.json")
                throw new InvalidDataException("The selected directory contains runtime state or credentials; choose a clean published package.");
        }
        if (entries.Count is 0 or > 10_000 || entries.ContainsKey("SHA256SUMS")) throw new InvalidDataException("The release hash manifest has an unsupported file count.");
        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("The publish cannot contain symbolic links or reparse points.");
            if ((entry.Attributes & FileAttributes.Directory) != 0) continue;
            var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
            if (relative == "SHA256SUMS") continue;
            if (!entries.TryGetValue(relative, out var expected)) throw new InvalidDataException("The publish contains a file absent from SHA256SUMS.");
            await using var input = File.OpenRead(entry.FullName);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
            if (hash != expected) throw new InvalidDataException("Release file hash mismatch: " + relative);
            observed.Add(relative);
        }
        if (observed.Count != entries.Count) throw new InvalidDataException("The publish is missing files listed in SHA256SUMS.");
        foreach (var required in new[] { "publish-info.json", "Workbench.Host", "Workbench.Host.dll", "Workbench.Protocol.dll", "Workbench.Host.deps.json", "Workbench.Host.runtimeconfig.json" })
            if (!entries.ContainsKey(required)) throw new InvalidDataException("The publish is missing a required Host asset: " + required);
        using var info = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "publish-info.json"), ct));
        var p = info.RootElement;
        var runtime = p.GetProperty("runtime").GetString();
        if (runtime is not ("linux-x64" or "linux-arm64" or "osx-x64" or "osx-arm64") || !p.GetProperty("selfContained").GetBoolean() ||
            p.GetProperty("hostExecutable").GetString() != "Workbench.Host" || p.GetProperty("formatVersion").GetInt32() != 1)
            throw new InvalidDataException("Choose a complete self-contained POSIX Host publish for the target RID.");
        if (NativeRuntime(Path.Combine(root, "Workbench.Host")) != runtime) throw new InvalidDataException("The native Host executable does not match its declared RID.");
        // Exact managed bytes bind target assets to this desktop build. The compiler normalizes
        // RID-only intermediate/PDB paths; no source-map or version-only executable fallback exists.
        if (!entries["Workbench.Host.dll"].Equals(expectedHostSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This prebuilt Host does not match the desktop's Host build. Publish both from the same build/configuration.");
        await using var protocol = File.OpenRead(typeof(Wire).Assembly.Location);
        var protocolHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(protocol, ct));
        if (entries["Workbench.Protocol.dll"] != protocolHash) throw new InvalidDataException("The prebuilt protocol assembly does not match this desktop build.");
        return new(root, runtime!, entries["Workbench.Host.dll"], entries);
    }

    private static string NativeRuntime(string path)
    {
        // The existing verify-release.py uses the same ELF/Mach-O header fields; BCL reads only these fixed fields.
        Span<byte> header = stackalloc byte[64];
        using var stream = File.OpenRead(path);
        stream.ReadExactly(header);
        if (header[..6].SequenceEqual(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1 }))
            return BinaryPrimitives.ReadUInt16LittleEndian(header[18..]) switch
            {
                62 => "linux-x64", 183 => "linux-arm64", _ => throw new InvalidDataException("Unsupported ELF architecture.")
            };
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) == 0xfeedfacf)
            return BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) switch
            {
                0x1000007 => "osx-x64", 0x100000c => "osx-arm64", _ => throw new InvalidDataException("Unsupported Mach-O architecture.")
            };
        throw new InvalidDataException("The Host does not contain a supported 64-bit POSIX executable.");
    }

    internal static async Task<string> MatchingHostHashAsync(CancellationToken ct = default)
    {
        var path = HostClient.LocateHost().HostPath;
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(Path.GetDirectoryName(path)!, "Workbench.Host.dll");
        await using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
    }

}
