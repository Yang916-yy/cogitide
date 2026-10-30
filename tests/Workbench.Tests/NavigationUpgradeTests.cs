using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class NavigationUpgradeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogito-navigation-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceService _service = new();
    public NavigationUpgradeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ReadPagesPreserveLongUnicodeLinesAndNewlinesWithoutUnreachableTail()
    {
        var content = new string('a', 11999) + "😀" + new string('中', 31000) + "\r\nsecond\rthird\n\nlast";
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(content)).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(_root, "long.txt"), bytes);
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        StringBuilder observed = new();
        var offset = 0;
        var character = 0;
        var calls = 0;
        do
        {
            var result = await _service.ReadFilePageAsync(_root, "long.txt", offset, 2, character, expectedHash);
            Assert.False(result.IsError, result.Content);
            Assert.True(result.Content.Length <= 80_000);
            using var page = JsonDocument.Parse(result.Content);
            var root = page.RootElement;
            Assert.Equal(expectedHash, root.GetProperty("sha256").GetString());
            Assert.Equal(bytes.Length, root.GetProperty("bytes").GetInt32());
            var text = root.GetProperty("content").GetString()!;
            Assert.NotEmpty(text);
            Assert.False(char.IsLowSurrogate(text[0]));
            Assert.False(char.IsHighSurrogate(text[^1]));
            observed.Append(text);
            if (root.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
            var next = root.GetProperty("nextOffset").GetInt32();
            var nextCharacter = root.GetProperty("nextCharacterOffset").GetInt32();
            Assert.True(next > offset || next == offset && nextCharacter > character);
            offset = next; character = nextCharacter;
            Assert.True(++calls < 20);
        } while (true);
        Assert.Equal(content, observed.ToString());
    }

    [Fact]
    public async Task PagedReadCanReachBeyondOldFileAndLineLimits()
    {
        var content = string.Concat(Enumerable.Repeat("a\n", 600_000));
        await Write("large.txt", content);
        using var page = JsonDocument.Parse((await _service.ReadFilePageAsync(_root, "large.txt", 599_999, 1)).Content);
        Assert.Equal("a\n", page.RootElement.GetProperty("content").GetString());
        Assert.Equal(600_000, page.RootElement.GetProperty("startLine").GetInt32());
        Assert.Equal(600_000, page.RootElement.GetProperty("endLine").GetInt32());
        Assert.False(page.RootElement.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task ReadContinuationRejectsChangedContentAndInvalidCursor()
    {
        await Write("sample.txt", "first\nsecond");
        using var page = JsonDocument.Parse((await _service.ReadFilePageAsync(_root, "sample.txt", limit: 1)).Content);
        var hash = page.RootElement.GetProperty("sha256").GetString();
        await Write("sample.txt", "FIRST\nsecond");
        var stale = await _service.ReadFilePageAsync(_root, "sample.txt", 1, expectedSha256: hash);
        Assert.True(stale.IsError);
        Assert.Contains("changed", stale.Content);
        Assert.True((await _service.ReadFilePageAsync(_root, "sample.txt", 10)).IsError);
        Assert.True((await _service.ReadFilePageAsync(_root, "sample.txt", characterOffset: 100)).IsError);
        await Write("emoji.txt", "😀tail");
        Assert.True((await _service.ReadFilePageAsync(_root, "emoji.txt", characterOffset: 1)).IsError);
    }

    [Fact]
    public async Task NavigationReadsEnforceUtf8BoundsAndCancellation()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "invalid.txt"), [0xC3, 0x28]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => _service.ReadFilePageAsync(_root, "invalid.txt"));
        await File.WriteAllBytesAsync(Path.Combine(_root, "utf16.txt"), Encoding.Unicode.GetBytes("hello"));
        await Assert.ThrowsAsync<IOException>(() => _service.ReadFilePageAsync(_root, "utf16.txt"));
        await using (var file = File.Create(Path.Combine(_root, "oversized.txt"))) file.SetLength(16 * 1024 * 1024 + 1);
        await Assert.ThrowsAsync<IOException>(() => _service.ReadFilePageAsync(_root, "oversized.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ReadFilePageAsync(_root, "../escape.txt"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.GlobFilesAsync(_root, "**/*", ".git/"));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.GlobFilesAsync(_root, "../*"));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ReadFilePageAsync(_root, "invalid.txt", ct: cancelled.Token));
    }

    [Fact]
    public async Task SearchDefaultsRemainLiteralInsensitiveAndRegexAddsContextAndGlob()
    {
        await Write("src/A.cs", "before\nclass Alpha {}\nafter\nliteral a+b\n");
        await Write("src/B.cs", "first\nsecond\nclass Beta {}\nlast\n");
        await Write("src/notes.txt", "class Outside {}\na+b");
        using var literal = JsonDocument.Parse((await _service.SearchFilePageAsync(_root, "A+B")).Content);
        Assert.Equal(2, literal.RootElement.GetProperty("matches").GetArrayLength());
        List<string> found = [];
        string? snapshot = null;
        var offset = 0;
        while (true)
        {
            var result = await _service.SearchFilePageAsync(_root, "^class (Alpha|Beta)", "src/", offset, 1,
                regex: true, glob: "**/*.cs", caseSensitive: true, contextLines: 1, expectedSnapshot: snapshot);
            Assert.False(result.IsError, result.Content);
            using var page = JsonDocument.Parse(result.Content);
            snapshot = page.RootElement.GetProperty("snapshot").GetString();
            var match = Assert.Single(page.RootElement.GetProperty("matches").EnumerateArray());
            found.Add(match.GetProperty("path").GetString()!);
            Assert.Equal(1, match.GetProperty("column").GetInt32());
            Assert.Single(match.GetProperty("before").EnumerateArray());
            Assert.Single(match.GetProperty("after").EnumerateArray());
            if (page.RootElement.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
            offset = page.RootElement.GetProperty("nextOffset").GetInt32();
        }
        Assert.Equal(["src/A.cs", "src/B.cs"], found);
        using var sensitive = JsonDocument.Parse((await _service.SearchFilePageAsync(_root, "CLASS", caseSensitive: true)).Content);
        Assert.Empty(sensitive.RootElement.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public async Task SearchHashDetectsSameLengthContentChangesBetweenPages()
    {
        await Write("a.txt", "needle a");
        await Write("b.txt", "needle b");
        using var first = JsonDocument.Parse((await _service.SearchFilePageAsync(_root, "needle", limit: 1)).Content);
        var snapshot = first.RootElement.GetProperty("snapshot").GetString();
        await Write("b.txt", "needle c");
        var stale = await _service.SearchFilePageAsync(_root, "needle", offset: 1, expectedSnapshot: snapshot);
        Assert.True(stale.IsError);
        Assert.Contains("changed", stale.Content);
    }

    [Fact]
    public async Task SearchLongLinesShowTheActualLateMatchAndUnicodePagesRemainValid()
    {
        await Write("long.txt", new string('x', 20000) + "NEEDLE" + new string('中', 500));
        using var late = JsonDocument.Parse((await _service.SearchFilePageAsync(_root, "needle")).Content);
        var match = Assert.Single(late.RootElement.GetProperty("matches").EnumerateArray());
        Assert.Equal(20001, match.GetProperty("column").GetInt32());
        Assert.Contains("NEEDLE", match.GetProperty("text").GetString());
        Assert.True(match.GetProperty("textTruncated").GetBoolean());
        await Write("unicode.txt", string.Join('\n', Enumerable.Range(0, 150).Select(i => $"命中{i}{new string('中', 500)}")));
        List<int> lines = [];
        var offset = 0;
        string? snapshot = null;
        while (true)
        {
            var result = await _service.SearchFilePageAsync(_root, "命中", offset: offset, limit: 1000, expectedSnapshot: snapshot);
            Assert.False(result.IsError, result.Content);
            Assert.True(result.Content.Length <= 80_000);
            using var page = JsonDocument.Parse(result.Content);
            snapshot = page.RootElement.GetProperty("snapshot").GetString();
            lines.AddRange(page.RootElement.GetProperty("matches").EnumerateArray().Select(item => item.GetProperty("line").GetInt32()));
            if (page.RootElement.GetProperty("nextOffset").ValueKind == JsonValueKind.Null) break;
            var next = page.RootElement.GetProperty("nextOffset").GetInt32();
            Assert.True(next > offset); offset = next;
        }
        Assert.Equal(Enumerable.Range(1, 150), lines);
    }

    [Fact]
    public async Task InvalidAndPathologicalRegexFailClearlyWithoutHangingOrClaimingNoMatches()
    {
        Assert.True((await _service.SearchFilePageAsync(_root, "[", regex: true)).IsError);
        await Write("adversarial.txt", new string('a', 100_000) + "!");
        var result = await _service.SearchFilePageAsync(_root, "^(a+)+$", regex: true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.IsError);
        Assert.Contains("budget", result.Content);
    }

    [Fact]
    public async Task GlobAndSearchPreserveGitIgnoresAndStableNamedPages()
    {
        await Git("init", "-q");
        await Write(".gitignore", "ignored/\n*.secret\n");
        await Write("ignored/hidden.cs", "needle");
        await Write("a.secret", "needle");
        await Write("src/A.cs", "needle");
        await Write("src/B.cs", "needle");
        await Write("other.cs", "needle");
        var inventory = await _service.InventoryAsync(_root, "src/");
        Assert.Equal("git", inventory.Source);
        Assert.Equal(["src/A.cs", "src/B.cs"], inventory.Files);
        using var first = JsonDocument.Parse((await _service.GlobFilesAsync(_root, "**/*.cs", "src/", limit: 1)).Content);
        Assert.Equal("src/A.cs", first.RootElement.GetProperty("files")[0].GetString());
        var snapshot = first.RootElement.GetProperty("snapshot").GetString();
        using var second = JsonDocument.Parse((await _service.GlobFilesAsync(_root, "**/*.cs", "src/", 1, 1, snapshot)).Content);
        Assert.Equal("src/B.cs", second.RootElement.GetProperty("files")[0].GetString());
        await Write("src/C.cs", "needle");
        Assert.True((await _service.GlobFilesAsync(_root, "**/*.cs", "src/", 1, 1, snapshot)).IsError);
        using var search = JsonDocument.Parse((await _service.SearchFilePageAsync(_root, "needle")).Content);
        Assert.DoesNotContain(search.RootElement.GetProperty("matches").EnumerateArray(), item => item.GetProperty("path").GetString()!.Contains("ignored", StringComparison.Ordinal));
        Assert.Equal(4, search.RootElement.GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public async Task ScanBudgetIsExplicitAndNarrowingCanReachPreviouslyUnscannedFiles()
    {
        for (var i = 0; i <= 2000; i++) await Write($"many/{i:D4}.txt", i == 2000 ? "target" : "other");
        using var broad = JsonDocument.Parse((await _service.SearchFilePageAsync(_root, "target")).Content);
        Assert.True(broad.RootElement.GetProperty("scanTruncated").GetBoolean());
        Assert.Equal(2000, broad.RootElement.GetProperty("scannedFiles").GetInt32());
        Assert.Empty(broad.RootElement.GetProperty("matches").EnumerateArray());
        using var narrow = JsonDocument.Parse((await _service.SearchFilePageAsync(_root, "target", "many/2000.txt")).Content);
        Assert.False(narrow.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Single(narrow.RootElement.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public async Task InventoryNarrowingKeepsExistingStringPrefixSemantics()
    {
        await Write("src/a.cs", "inside");
        await Write("src2/b.cs", "sibling prefix");
        await Write("other/c.cs", "outside");
        var prefix = await _service.InventoryAsync(_root, "src");
        Assert.Equal(["src/a.cs", "src2/b.cs"], prefix.Files);
        var directory = await _service.InventoryAsync(_root, "src/");
        Assert.Equal(["src/a.cs"], directory.Files);
    }

    private async Task Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    private async Task Git(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(process.ExitCode == 0, await error);
        await output;
    }

    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(parent, comparison) || !Path.GetFileName(full).StartsWith("cogito-navigation-", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected fixture path.");
        if (!Directory.Exists(full)) return;
        foreach (var file in Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(full, true);
    }
}
