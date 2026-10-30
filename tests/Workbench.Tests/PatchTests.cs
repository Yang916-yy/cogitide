using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class PatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogito-patch-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceService _service = new();
    public PatchTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task AddsUpdatesDeletesAndMovesWithoutGitAndReportsEveryChangedPath()
    {
        await Write("src/update.cs", "public class Before {}\n");
        await Write("src/old.txt", "move this\n");
        await Write("remove.txt", "delete this\n");
        const string patch = """
            *** Begin Patch
            *** Add File: docs/new.md
            +# 新增
            +Hello
            *** Update File: src/update.cs
            @@
            -public class Before {}
            +public class After {}
            *** Update File: src/old.txt
            *** Move to: renamed/new.txt
            @@
            -move this
            +moved and edited
            *** Delete File: remove.txt
            *** End Patch
            """;
        Assert.Equal(new[] { "docs/new.md", "src/update.cs", "src/old.txt", "renamed/new.txt", "remove.txt" }, WorkspaceService.DescribePatchPaths(_root, patch));
        var result = await _service.ApplyPatchAsync(_root, patch, Hashes("src/update.cs", "src/old.txt", "remove.txt"));
        Assert.False(result.IsError, result.Content);
        Assert.Equal("# 新增\nHello\n", await Read("docs/new.md"));
        Assert.Equal("public class After {}\n", await Read("src/update.cs"));
        Assert.Equal("moved and edited\n", await Read("renamed/new.txt"));
        Assert.False(File.Exists(Full("src/old.txt")));
        Assert.False(File.Exists(Full("remove.txt")));
        using var json = JsonDocument.Parse(result.Content);
        var changes = json.RootElement.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(5, changes.Length);
        Assert.Equal("src/old.txt", changes.Single(change => change.GetProperty("path").GetString() == "renamed/new.txt").GetProperty("sourcePath").GetString());
        foreach (var change in changes)
        {
            var path = change.GetProperty("path").GetString()!;
            if (change.GetProperty("operation").GetString() == "delete") Assert.Equal(JsonValueKind.Null, change.GetProperty("sha256").ValueKind);
            else Assert.Equal(Hashes(path)[path], change.GetProperty("sha256").GetString());
        }
        AssertNoStaging();
    }

    [Theory]
    [InlineData("first\r\nold\r\nlast\r\n", "first\r\n新\r\nlast\r\n")]
    [InlineData("first\nold\nlast", "first\n新\nlast")]
    [InlineData("first\r\nold\nlast", "first\r\n新\nlast")]
    [InlineData("first\rold\rlast\r", "first\r新\rlast\r")]
    public async Task KeepsBomUnicodeUnchangedLineEndingsAndFinalNewlineState(string before, string expected)
    {
        await File.WriteAllTextAsync(Full("源.txt"), before, new UTF8Encoding(true));
        var result = await _service.ApplyPatchAsync(_root, Patch("*** Update File: 源.txt\n@@\n first\n-old\n+新\n last"), Hashes("源.txt"));
        Assert.False(result.IsError, result.Content);
        Assert.Equal(expected, await Read("源.txt"));
        Assert.Equal(new byte[] { 239, 187, 191 }, (await File.ReadAllBytesAsync(Full("源.txt")))[..3]);
    }

    [Fact]
    public async Task MultipleOrderedHunksUseContextAndEofAnchor()
    {
        await Write("a.txt", "first\nsame\nmiddle\nsame\nlast\n");
        var result = await _service.ApplyPatchAsync(_root, Patch("*** Update File: a.txt\n@@ first\n-same\n+one\n middle\n@@\n-same\n-last\n+two\n+end\n*** End of File"), Hashes("a.txt"));
        Assert.False(result.IsError, result.Content);
        Assert.Equal("first\none\nmiddle\ntwo\nend\n", await Read("a.txt"));
    }

    [Fact]
    public async Task PureMovePreservesBytesAndPureAdditionAppendsAtEof()
    {
        await File.WriteAllTextAsync(Full("a.txt"), "原文\r\nlast", new UTF8Encoding(true));
        var original = await File.ReadAllBytesAsync(Full("a.txt"));
        var moved = await _service.ApplyPatchAsync(_root, Patch("*** Update File: a.txt\n*** Move to: b.txt"), Hashes("a.txt"));
        Assert.False(moved.IsError, moved.Content);
        Assert.Equal(original, await File.ReadAllBytesAsync(Full("b.txt")));
        var appended = await _service.ApplyPatchAsync(_root, Patch("*** Update File: b.txt\n@@\n+tail"), Hashes("b.txt"));
        Assert.False(appended.IsError, appended.Content);
        Assert.Equal("原文\r\nlast\r\ntail", await Read("b.txt"));
    }

    [Theory]
    [InlineData("missing-hash")]
    [InlineData("stale-hash")]
    [InlineData("bad-last-context")]
    [InlineData("existing-add")]
    [InlineData("existing-move")]
    public async Task EntirePatchIsPreflightedBeforeAnyTargetOrNewDirectoryIsWritten(string defect)
    {
        await Write("a.txt", "before\n");
        await Write("b.txt", "before\n");
        var hashes = Hashes("a.txt", "b.txt");
        var body = "*** Add File: new/nested/a.txt\n+new\n*** Update File: a.txt\n@@\n-before\n+after\n*** Update File: b.txt\n@@\n-before\n+after";
        if (defect == "missing-hash") hashes.Remove("b.txt");
        if (defect == "stale-hash") hashes["b.txt"] = new('0', 64);
        if (defect == "bad-last-context") body = body[..body.LastIndexOf("-before", StringComparison.Ordinal)] + "-missing\n+after";
        if (defect == "existing-add") { await Write("occupied.txt", "keep\n"); body += "\n*** Add File: occupied.txt\n+overwrite"; }
        if (defect == "existing-move") { hashes.Remove("b.txt"); body = "*** Add File: new/nested/a.txt\n+new\n*** Update File: a.txt\n*** Move to: b.txt"; }
        var result = await _service.ApplyPatchAsync(_root, Patch(body), hashes);
        Assert.True(result.IsError);
        Assert.Equal("before\n", await Read("a.txt"));
        Assert.Equal("before\n", await Read("b.txt"));
        Assert.False(Directory.Exists(Full("new")));
        AssertNoStaging();
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("dir/../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:\\outside.txt")]
    [InlineData(".git/config")]
    [InlineData("dir//a.txt")]
    [InlineData("file.txt:stream")]
    [InlineData("NUL.txt")]
    [InlineData("trailing. /a.txt")]
    public async Task InvalidPathsCannotEscapeOrAliasWorkspaceTargets(string path)
    {
        var patch = Patch("*** Add File: " + path + "\n+unsafe");
        Assert.True((await _service.ApplyPatchAsync(_root, patch, new Dictionary<string, string>())).IsError);
        Assert.ThrowsAny<Exception>(() => WorkspaceService.DescribePatchPaths(_root, patch));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Theory]
    [InlineData("*** Add File: a.txt\nnot-plus")]
    [InlineData("*** Update File: a.txt\n@@")]
    [InlineData("*** Delete File: a.txt\nignored trailing content")]
    [InlineData("*** Add File: a.txt\n+a\n*** Add File: a.txt\n+b")]
    [InlineData("*** Add File: dir\n+a\n*** Add File: dir/a.txt\n+b")]
    [InlineData("*** Unknown: a.txt")]
    [InlineData("*** Update File: a.txt\n@@\n-a\n+b\n*** End of File\n@@\n-b\n+c")]
    public async Task MalformedOrOverlappingOperationsAreRejectedCompletely(string body)
    {
        await Write("a.txt", "a\n");
        var result = await _service.ApplyPatchAsync(_root, Patch(body), Hashes("a.txt"));
        Assert.True(result.IsError);
        Assert.Equal("a\n", await Read("a.txt"));
        AssertNoStaging();
    }

    [Theory]
    [InlineData("same\nsame\n", "-same\n+other")]
    [InlineData("  same\n", "-same\n+other")]
    [InlineData("‘quoted’\n", "-'quoted'\n+other")]
    [InlineData("same\nnext\n", "-same\n+other\n*** End of File")]
    public async Task RejectsAmbiguousFuzzyOrNonTerminalMatches(string before, string body)
    {
        await Write("a.txt", before);
        var result = await _service.ApplyPatchAsync(_root, Patch("*** Update File: a.txt\n@@\n" + body), Hashes("a.txt"));
        Assert.True(result.IsError);
        Assert.Equal(before, await Read("a.txt"));
    }

    [Fact]
    public async Task StaleReadAfterReviewPreservesConcurrentUserEditAndOtherFiles()
    {
        await Write("a.txt", "before\n"); await Write("b.txt", "before\n");
        var hashes = Hashes("a.txt", "b.txt");
        var patch = Patch("*** Update File: a.txt\n@@\n-before\n+after\n*** Update File: b.txt\n@@\n-before\n+after");
        Assert.Equal(2, WorkspaceService.DescribePatchPaths(_root, patch).Count);
        await Write("b.txt", "user changed while approval was open\n");
        Assert.True((await _service.ApplyPatchAsync(_root, patch, hashes)).IsError);
        Assert.Equal("before\n", await Read("a.txt"));
        Assert.Equal("user changed while approval was open\n", await Read("b.txt"));
    }

    [Fact]
    public async Task RealFilesystemCommitFailureRollsBackEarlierUpdateMoveAndAdd()
    {
        if (!OperatingSystem.IsWindows()) return; // Windows sharing denies replacement after successful readable preflight.
        await Write("a.txt", "before\n"); await Write("move.txt", "move\n"); await Write("locked.txt", "locked\n");
        var hashes = Hashes("a.txt", "move.txt", "locked.txt");
        using var locked = new FileStream(Full("locked.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        var result = await _service.ApplyPatchAsync(_root, Patch("*** Update File: a.txt\n@@\n-before\n+after\n*** Update File: move.txt\n*** Move to: new/moved.txt\n*** Add File: new/added.txt\n+new\n*** Update File: locked.txt\n@@\n-locked\n+changed"), hashes);
        Assert.True(result.IsError);
        Assert.Contains("rolled back", result.Content);
        Assert.Equal("before\n", await Read("a.txt"));
        Assert.Equal("move\n", await Read("move.txt"));
        Assert.Equal("locked\n", await Read("locked.txt"));
        Assert.False(Directory.Exists(Full("new")));
        AssertNoStaging();
    }

    [Fact]
    public async Task CancellationAndOversizedPatchDoNotStartMutatingFiles()
    {
        await Write("a.txt", "before\n");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.ApplyPatchAsync(_root, Patch("*** Delete File: a.txt"), Hashes("a.txt"), stop.Token));
        Assert.True((await _service.ApplyPatchAsync(_root, Patch("*** Add File: too-big.txt\n+" + new string('x', WorkspaceService.MaxFileBytes + 1)), new Dictionary<string, string>())).IsError);
        Assert.Equal("before\n", await Read("a.txt")); Assert.False(File.Exists(Full("too-big.txt"))); AssertNoStaging();
    }

    [Fact]
    public async Task OversizedChangeMetadataIsRejectedBeforeCreatingDirectories()
    {
        var prefix = string.Concat(Enumerable.Repeat("长目录/", 100));
        var body = string.Join('\n', Enumerable.Range(0, 64).Select(index => "*** Add File: " + prefix + index + ".txt\n+new"));
        var result = await _service.ApplyPatchAsync(_root, Patch(body), new Dictionary<string, string>());
        Assert.True(result.IsError);
        Assert.Contains("metadata", result.Content);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task SymlinkAndDanglingSymlinkAreNeverFollowedOrOverwritten()
    {
        await Write("real.txt", "before\n");
        try { File.CreateSymbolicLink(Full("link.txt"), Full("real.txt")); File.CreateSymbolicLink(Full("dangling.txt"), Full("missing.txt")); }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or PlatformNotSupportedException) { return; }
        Assert.True((await _service.ApplyPatchAsync(_root, Patch("*** Add File: dangling.txt\n+unsafe"), new Dictionary<string, string>())).IsError);
        Assert.True((await _service.ApplyPatchAsync(_root, Patch("*** Update File: link.txt\n@@\n-before\n+unsafe"), Hashes("real.txt"))).IsError);
        Assert.Equal("before\n", await Read("real.txt"));
        Assert.False(File.Exists(Full("missing.txt")));
    }

    [Fact]
    public async Task ExistingUnixModesSurviveUpdateAndMove()
    {
        if (OperatingSystem.IsWindows()) return;
        await Write("a.sh", "echo before\n");
        const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(Full("a.sh"), mode);
        var result = await _service.ApplyPatchAsync(_root, Patch("*** Update File: a.sh\n*** Move to: bin/a.sh\n@@\n-echo before\n+echo after"), Hashes("a.sh"));
        Assert.False(result.IsError, result.Content);
        Assert.Equal(mode, File.GetUnixFileMode(Full("bin/a.sh")));
    }

    private string Full(string path) => Path.Combine(_root, path);
    private async Task Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(Full(path))!); await File.WriteAllTextAsync(Full(path), text, new UTF8Encoding(false)); }
    private Task<string> Read(string path) => File.ReadAllTextAsync(Full(path));
    private Dictionary<string, string> Hashes(params string[] paths) => paths.ToDictionary(path => path, path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Full(path)))));
    private static string Patch(string body) => "*** Begin Patch\n" + body + "\n*** End Patch\n";
    private void AssertNoStaging() => Assert.Empty(Directory.EnumerateFiles(_root, ".cogito-patch-*", SearchOption.AllDirectories));
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }
}
