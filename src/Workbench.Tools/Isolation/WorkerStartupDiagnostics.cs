using System.Text;
using System.Text.RegularExpressions;

namespace Workbench.Tools;

/// <summary>Bounded, explicitly untrusted startup diagnostics; never captures a broker environment or account store.</summary>
public sealed class WorkerStartupDiagnostics(IEnumerable<string> privatePaths)
{
    private const int RetainedCharacters = 4096;
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();
    private readonly string[] _privatePaths = privatePaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(p => p.Length).ToArray();

    public async Task DrainAsync(Stream error)
    {
        using var reader = new StreamReader(error, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var buffer = new char[512];
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory());
                if (count == 0) return;
                lock (_gate)
                {
                    var retained = Math.Min(count, RetainedCharacters - _text.Length);
                    if (retained > 0) _text.Append(buffer, 0, retained);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    public string Summary()
    {
        string value;
        lock (_gate) value = _text.ToString();
        foreach (var path in _privatePaths) value = value.Replace(path, "<private-path>", StringComparison.OrdinalIgnoreCase);
        value = new string(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        try
        {
            value = Regex.Replace(value, @"(?i)(bearer\s+)[^\s]+", "$1<redacted>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            value = Regex.Replace(value, @"(?i)(api[-_]?key|access[-_]?token|password|secret)(\s*[:=]\s*)[^\s,;]+", "$1$2<redacted>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch (RegexMatchTimeoutException) { return "Startup stderr was omitted because redaction exceeded its bounded budget."; }
        return value.Length == 0 ? "No startup stderr was observed." : "Untrusted worker stderr: " + value[..Math.Min(2048, value.Length)];
    }
}
