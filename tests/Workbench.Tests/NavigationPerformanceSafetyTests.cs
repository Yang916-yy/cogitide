using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

/// <summary>Content and pagination invariants for pooled reads and deferred search context.</summary>
public sealed class NavigationPerformanceSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-navigation-safety-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceService _service = new();
    public NavigationPerformanceSafetyTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ReadsHashEveryRawByteAndDecodeBomAndMultibyteBoundariesStrictly()
    {
        var content = new string('a', 16_379) + "😀中\r\n" + new string('b', 49_146) + "\uFEFF終";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(_root, "unicode.txt"), bytes);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var first = Json(await _service.ReadFilePageAsync(_root, "unicode.txt", limit: 1));
        Assert.Equal(expectedHash, first.RootElement.GetProperty("sha256").GetString());
        Assert.Equal(bytes.Length, first.RootElement.GetProperty("bytes").GetInt32());
        using var search = Json(await _service.SearchFilePageAsync(_root, "😀中", caseSensitive: true));
        var match = Assert.Single(search.RootElement.GetProperty("matches").EnumerateArray());
        Assert.Equal(16_380, match.GetProperty("column").GetInt32());
        Assert.Equal(expectedHash, match.GetProperty("sha256").GetString());
        Assert.Contains("😀中", match.GetProperty("text").GetString());
        using var tail = Json(await _service.ReadFilePageAsync(_root, "unicode.txt", offset: 1, characterOffset: 49_146));
        Assert.Equal("\uFEFF終", tail.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ReusedReadBuffersNeverExposeDataBeyondTheCurrentFileLength()
    {
        foreach (var content in new[] { new string('x', 20_000) + "stale-tail", new string('y', 17_000), "short", "", "\uFEFF" })
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            await File.WriteAllBytesAsync(Path.Combine(_root, "changing.txt"), bytes);
            using var page = Json(await _service.ReadFilePageAsync(_root, "changing.txt", offset: 0, limit: 1));
            Assert.Equal(bytes.Length, page.RootElement.GetProperty("bytes").GetInt32());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), page.RootElement.GetProperty("sha256").GetString());
            var text = page.RootElement.GetProperty("content").GetString()!;
            Assert.Equal(content == "\uFEFF" ? "" : content[..Math.Min(content.Length, 12_000)], text);
            if (!content.Contains("stale-tail", StringComparison.Ordinal)) Assert.DoesNotContain("stale-tail", text);
        }
    }

    [Fact]
    public async Task InvalidUtf8AtTheEndInvalidatesTheWholeReadAndSkipsTheWholeSearchFile()
    {
        var bytes = Encoding.UTF8.GetBytes("needle\n" + new string('a', 30_000)).Concat(new byte[] { 0xF0, 0x9F, 0x98 }).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(_root, "invalid.txt"), bytes);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => _service.ReadFilePageAsync(_root, "invalid.txt", limit: 1));
        using var search = Json(await _service.SearchFilePageAsync(_root, "needle", limit: 1));
        Assert.Equal(1, search.RootElement.GetProperty("skippedFiles").GetInt32());
        Assert.Equal(0, search.RootElement.GetProperty("totalMatchesInScan").GetInt32());
        Assert.True(search.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Empty(search.RootElement.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public async Task NonmatchingSameLengthEditsStillInvalidateSearchSnapshots()
    {
        await Write("a.txt", "needle one");
        await Write("b.txt", "needle two");
        await Write("z.txt", new string('x', 50_000) + "alpha");
        using var first = Json(await _service.SearchFilePageAsync(_root, "needle", limit: 1));
        var snapshot = first.RootElement.GetProperty("snapshot").GetString();
        var previousWrite = File.GetLastWriteTimeUtc(Path.Combine(_root, "z.txt"));
        await Write("z.txt", new string('x', 50_000) + "omega");
        File.SetLastWriteTimeUtc(Path.Combine(_root, "z.txt"), previousWrite);
        var stale = await _service.SearchFilePageAsync(_root, "needle", offset: 1, limit: 1, expectedSnapshot: snapshot);
        Assert.True(stale.IsError);
        Assert.Contains("changed", stale.Content);
        using var refreshed = Json(await _service.SearchFilePageAsync(_root, "needle", offset: 1, limit: 1));
        Assert.NotEqual(snapshot, refreshed.RootElement.GetProperty("snapshot").GetString());
        Assert.Equal(2, refreshed.RootElement.GetProperty("totalMatchesInScan").GetInt32());
        Assert.Single(refreshed.RootElement.GetProperty("matches").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredContextsRetainMixedNewlinesRingOrderAndUnicodeBoundaries(bool regex)
    {
        var longContext = new string('x', 499) + "😀tail";
        await Write("lines.txt", "first\r\n" + longContext + "\r\n\nbefore two\r\n😀MaTcH one\nafter one\r" + longContext + "\r\nMATCH two\n");
        var query = regex ? "(?i:match)" : "match";
        using var first = Json(await _service.SearchFilePageAsync(_root, query, limit: 1, regex: regex, contextLines: 2));
        var match = Assert.Single(first.RootElement.GetProperty("matches").EnumerateArray());
        Assert.Equal(5, match.GetProperty("line").GetInt32());
        Assert.Equal(3, match.GetProperty("column").GetInt32());
        Assert.Equal(2, first.RootElement.GetProperty("totalMatchesInScan").GetInt32());
        Assert.Equal(new[] { 3, 4 }, match.GetProperty("before").EnumerateArray().Select(line => line.GetProperty("line").GetInt32()));
        Assert.Equal(new[] { "", "before two" }, match.GetProperty("before").EnumerateArray().Select(line => line.GetProperty("text").GetString()));
        Assert.Equal(new[] { 6, 7 }, match.GetProperty("after").EnumerateArray().Select(line => line.GetProperty("line").GetInt32()));
        var clipped = match.GetProperty("after")[1];
        Assert.Equal(new string('x', 499), clipped.GetProperty("text").GetString());
        Assert.True(clipped.GetProperty("truncated").GetBoolean());
        var snapshot = first.RootElement.GetProperty("snapshot").GetString();
        using var second = Json(await _service.SearchFilePageAsync(_root, query, offset: 1, limit: 1, regex: regex,
            contextLines: 2, expectedSnapshot: snapshot));
        var next = Assert.Single(second.RootElement.GetProperty("matches").EnumerateArray());
        Assert.Equal(8, next.GetProperty("line").GetInt32());
        Assert.Equal(new[] { 6, 7 }, next.GetProperty("before").EnumerateArray().Select(line => line.GetProperty("line").GetInt32()));
        Assert.Empty(next.GetProperty("after").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, second.RootElement.GetProperty("nextOffset").ValueKind);
    }

    [Fact]
    public async Task FillingThePageDoesNotSuppressLaterRegexTimeouts()
    {
        await Write("later-timeout.txt", "ok\n" + new string('a', 100_000) + "!");
        var result = await _service.SearchFilePageAsync(_root, "^ok$|^(a+)+$", limit: 1, regex: true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsError);
        Assert.Contains("100 ms", result.Content);
    }

    [Fact]
    public async Task GitNulInventoryKeepsUnicodeWhitespaceAndPrefixBoundaries()
    {
        await Git("init", "-q");
        await Write(".gitignore", "*.secret\n");
        await Write("src/a 空格.cs", "one");
        await Write("src/z.cs", "two");
        await Write("src/hidden.secret", "ignored");
        await Write("src2/b.cs", "three");
        var directory = await _service.InventoryAsync(_root, "src/");
        Assert.Equal("git", directory.Source);
        Assert.Equal(new[] { "src/a 空格.cs", "src/z.cs" }, directory.Files);
        var prefix = await _service.InventoryAsync(_root, "src");
        Assert.Equal(new[] { "src/a 空格.cs", "src/z.cs", "src2/b.cs" }, prefix.Files);
    }

    private async Task Write(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
    }

    private async Task Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var variable in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(variable);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        Assert.True(process.ExitCode == 0, await stderr);
    }

    private static JsonDocument Json(ToolResult result)
    {
        Assert.False(result.IsError, result.Content);
        Assert.InRange(result.Content.Length, 1, 80_000);
        return JsonDocument.Parse(result.Content);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
