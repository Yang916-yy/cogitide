using System.Diagnostics;
using System.Text;

namespace Workbench.Desktop.Remote;

internal static class WslDistributions
{
    public static Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("WSL discovery is available on Windows only.");
        var start = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--list");
        start.ArgumentList.Add("--quiet");
        return ListAsync(start, cancellationToken);
    }

    internal static async Task<IReadOnlyList<string>> ListAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = Process.Start(start) ?? throw new IOException("Unable to start WSL discovery.");
        try
        {
            var output = ReadBoundedAsync(process.StandardOutput.BaseStream, timeout.Token);
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("WSL discovery failed. Install WSL and a distribution before connecting.");
            return Parse(output.Result);
        }
        finally { ProcessShutdown.Terminate(process); }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[2048];
        while (await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) is var count && count > 0)
        {
            if (output.Length + count > 65_536) throw new InvalidDataException("WSL discovery returned too much output.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    internal static IReadOnlyList<string> Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 65_536) throw new InvalidDataException("WSL discovery returned too much output.");
        // wsl.exe commonly writes UTF-16LE even through a redirected pipe, with or without a BOM.
        var utf16 = bytes.StartsWith(new byte[] { 0xff, 0xfe }) ||
                    (bytes.Length % 2 == 0 && (bytes.IndexOf(new byte[] { 10, 0 }) >= 0 || bytes.IndexOf((byte)0) >= 0));
        var value = (utf16 ? Encoding.Unicode : Encoding.UTF8).GetString(bytes).Replace("\0", "", StringComparison.Ordinal);
        var distributions = value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim('\uFEFF', ' ', '\t', '\r'))
            .Where(line => line.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (distributions.Length > 128 || distributions.Any(name => name.Length > 255 || name.StartsWith('-') || name.Any(char.IsControl)))
            throw new InvalidDataException("WSL discovery returned an invalid distribution list.");
        return distributions;
    }
}
