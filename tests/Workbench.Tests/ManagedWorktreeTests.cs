using System.Diagnostics;
using System.Text.Json;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

[CollectionDefinition("Managed worktree Git environment", DisableParallelization = true)]
public sealed class ManagedWorktreeGitEnvironmentCollection { }

[Collection("Managed worktree Git environment")]
public sealed class ManagedWorktreeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-worktrees-" + Guid.NewGuid().ToString("N"));
    private string Repository => Path.Combine(_root, "repository with spaces");
    private readonly WorkspaceService _service = new();
    public ManagedWorktreeTests() => Directory.CreateDirectory(Repository);

    [Fact]
    public async Task GitOwnsIsolatedCheckoutStatusAndCleanRemovalWhileBranchIsRetained()
    {
        await Initialize();
        var created = await _service.CreateWorktreeAsync(Repository, "owner-chat");
        Assert.False(created.IsError, created.Content);
        var record = Json(created);
        var id = record.GetProperty("worktreeId").GetString()!;
        var path = record.GetProperty("path").GetString()!;
        var branch = record.GetProperty("branch").GetString()!;
        Assert.False(path.StartsWith(Repository + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("initial", await File.ReadAllTextAsync(Path.Combine(path, "source.txt")));
        Assert.True(Json(await _service.WorktreeStatusAsync(Repository, id)).GetProperty("clean").GetBoolean());
        // Selecting the managed checkout resolves the same Git common directory and management bucket.
        Assert.Single(Json(await _service.ListWorktreesAsync(path)).GetProperty("worktrees").EnumerateArray());
        await File.WriteAllTextAsync(Path.Combine(path, "source.txt"), "isolated-change");
        Assert.Equal("initial", await File.ReadAllTextAsync(Path.Combine(Repository, "source.txt")));
        Assert.False(Json(await _service.WorktreeStatusAsync(Repository, id)).GetProperty("clean").GetBoolean());
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RemoveWorktreeAsync(Repository, "owner-chat", id));
        Assert.True(Directory.Exists(path));
        await File.WriteAllTextAsync(Path.Combine(path, "source.txt"), "initial");
        await _service.RemoveWorktreeAsync(Repository, "owner-chat", id);
        Assert.False(Directory.Exists(path));
        Assert.Empty(Json(await _service.ListWorktreesAsync(Repository)).GetProperty("worktrees").EnumerateArray());
        await Git(Repository, "show-ref", "--verify", "refs/heads/" + branch);
    }

    [Theory]
    [InlineData("untracked.txt")]
    [InlineData("ignored.txt")]
    public async Task RemovalNeverDiscardsUntrackedOrIgnoredFiles(string file)
    {
        await Initialize();
        var record = Json(await _service.CreateWorktreeAsync(Repository, "owner-chat"));
        var id = record.GetProperty("worktreeId").GetString()!;
        var path = record.GetProperty("path").GetString()!;
        await File.WriteAllTextAsync(Path.Combine(path, file), "preserve this content");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.RemoveWorktreeAsync(Repository, "owner-chat", id));
        Assert.Equal("preserve this content", await File.ReadAllTextAsync(Path.Combine(path, file)));
        File.Delete(Path.Combine(path, file));
        await _service.RemoveWorktreeAsync(Repository, "owner-chat", id);
    }

    [Fact]
    public async Task AnotherChatRepositoryOrForgedPathCannotRemoveManagedCheckout()
    {
        await Initialize();
        var record = Json(await _service.CreateWorktreeAsync(Repository, "owner-chat"));
        var id = record.GetProperty("worktreeId").GetString()!;
        var path = record.GetProperty("path").GetString()!;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RemoveWorktreeAsync(Repository, "foreign-chat", id));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.RemoveWorktreeAsync(Repository, "owner-chat", "../../source"));
        var other = Path.Combine(_root, "other-repository");
        Directory.CreateDirectory(other);
        await Git(other, "init");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.RemoveWorktreeAsync(other, "owner-chat", id));
        var manifest = Path.Combine(Directory.GetParent(path)!.FullName, ".metadata", id + ".json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(manifest));
        var forged = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        forged["path"] = JsonSerializer.SerializeToElement(Repository);
        await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(forged));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.RemoveWorktreeAsync(Repository, "owner-chat", id));
        Assert.True(File.Exists(Path.Combine(Repository, "source.txt")));
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task InvalidStartPointAndBranchAreRejectedWithoutCreatingAWorktree()
    {
        await Initialize();
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateWorktreeAsync(Repository, "owner-chat", "--force"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateWorktreeAsync(Repository, "owner-chat", startPoint: "missing-ref"));
        Assert.Empty(Json(await _service.ListWorktreesAsync(Repository)).GetProperty("worktrees").EnumerateArray());
        Assert.False(Directory.Exists(Path.Combine(_root, ".cogitide-worktrees")));
    }

    [Fact]
    public async Task BareAndGitInternalSelectionsCannotCreateManagedCheckouts()
    {
        await Initialize();
        foreach (var path in new[] { Path.Combine(Repository, ".git"), Path.Combine(Repository, ".git", "objects") })
            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateWorktreeAsync(path, "owner-chat"));
        var bare = Path.Combine(_root, "bare.git");
        Directory.CreateDirectory(bare);
        await Git(bare, "init", "--bare");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateWorktreeAsync(bare, "owner-chat"));
        Assert.False(Directory.Exists(Path.Combine(_root, ".cogitide-worktrees")));
    }

    [Fact]
    public async Task SelectedSubdirectoriesAreAllowedButControlCharacterPathsAreRejected()
    {
        await Initialize();
        var subdirectory = Path.Combine(Repository, "source-subdirectory");
        Directory.CreateDirectory(subdirectory);
        var record = Json(await _service.CreateWorktreeAsync(subdirectory, "owner-chat"));
        var id = record.GetProperty("worktreeId").GetString()!;
        Assert.True(Json(await _service.WorktreeStatusAsync(subdirectory, id)).GetProperty("clean").GetBoolean());
        await _service.RemoveWorktreeAsync(subdirectory, "owner-chat", id);
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateWorktreeAsync(Repository + "\n", "owner-chat"));
    }

    [Fact]
    public async Task ManagedBucketAncestorJunctionCannotRedirectCreationOrListing()
    {
        await Initialize();
        var outside = Path.Combine(_root, "physical-outside-target");
        Directory.CreateDirectory(outside);
        var marker = Path.Combine(outside, "preserve.txt");
        await File.WriteAllTextAsync(marker, "preserve outside content");
        var link = Path.Combine(_root, ".cogitide-worktrees");
        await CreateDirectoryLinkAsync(link, outside);
        try
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.CreateWorktreeAsync(Repository, "owner-chat"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ListWorktreesAsync(Repository));
            Assert.Equal("preserve outside content", await File.ReadAllTextAsync(marker));
            Assert.Single(Directory.EnumerateFileSystemEntries(outside));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task AmbientGitOverridesCannotRedirectSourceOrManagedCheckout()
    {
        await Initialize();
        var foreign = Path.Combine(_root, "foreign repository");
        Directory.CreateDirectory(foreign);
        await Git(foreign, "init");
        var sentinel = Path.Combine(foreign, "outside.txt");
        var index = Path.Combine(foreign, "do-not-use.index");
        await File.WriteAllTextAsync(sentinel, "foreign content must remain unchanged");
        await File.WriteAllTextAsync(index, "not a Git index; never overwrite");
        var foreignHead = await File.ReadAllTextAsync(Path.Combine(foreign, ".git", "HEAD"));
        var poisoned = new Dictionary<string, string>
        {
            ["GIT_DIR"] = Path.Combine(foreign, ".git"),
            ["GIT_COMMON_DIR"] = Path.Combine(foreign, ".git"),
            ["GIT_WORK_TREE"] = foreign,
            ["GIT_INDEX_FILE"] = index,
            ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "core.bare", ["GIT_CONFIG_VALUE_0"] = "true"
        };
        var original = poisoned.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (name, value) in poisoned) Environment.SetEnvironmentVariable(name, value);
            var created = await _service.CreateWorktreeAsync(Repository, "owner-chat");
            Assert.False(created.IsError, created.Content);
            var record = Json(created);
            var id = record.GetProperty("worktreeId").GetString()!;
            var path = record.GetProperty("path").GetString()!;
            Assert.Equal("initial", await File.ReadAllTextAsync(Path.Combine(path, "source.txt")));
            Assert.True(Json(await _service.WorktreeStatusAsync(Repository, id)).GetProperty("clean").GetBoolean());
            Assert.Single(Json(await _service.ListWorktreesAsync(Repository)).GetProperty("worktrees").EnumerateArray());
            await _service.RemoveWorktreeAsync(Repository, "owner-chat", id);
            Assert.Equal("foreign content must remain unchanged", await File.ReadAllTextAsync(sentinel));
            Assert.Equal("not a Git index; never overwrite", await File.ReadAllTextAsync(index));
            Assert.Equal(foreignHead, await File.ReadAllTextAsync(Path.Combine(foreign, ".git", "HEAD")));
        }
        finally { foreach (var (name, value) in original) Environment.SetEnvironmentVariable(name, value); }
    }

    [Fact]
    public async Task ManagedCheckoutDoesNotInvokeTheRepositoryPostCheckoutHook()
    {
        await Initialize();
        var hooks = Path.Combine(Repository, ".git", "fixture-hooks");
        Directory.CreateDirectory(hooks);
        var hook = Path.Combine(hooks, "post-checkout");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nprintf 'unexpected hook execution' > .cogitide-hook-ran\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Git(Repository, "config", "core.hooksPath", hooks);
        var created = await _service.CreateWorktreeAsync(Repository, "owner-chat");
        Assert.False(created.IsError, created.Content);
        var record = Json(created);
        var id = record.GetProperty("worktreeId").GetString()!;
        var path = record.GetProperty("path").GetString()!;
        Assert.False(File.Exists(Path.Combine(path, ".cogitide-hook-ran")));
        Assert.True(Json(await _service.WorktreeStatusAsync(Repository, id)).GetProperty("clean").GetBoolean());
        // Prove the installed hook is executable on this platform rather than accepting a
        // broken fixture as suppression evidence. An ordinary explicit Git checkout runs it.
        await Git(path, "checkout", "--detach", "HEAD");
        var marker = Path.Combine(path, ".cogitide-hook-ran");
        Assert.Equal("unexpected hook execution", await File.ReadAllTextAsync(marker));
        File.Delete(marker);
        await _service.RemoveWorktreeAsync(Repository, "owner-chat", id);
    }

    private static async Task CreateDirectoryLinkAsync(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        // Reuse the existing ProjectRulesTests fixture approach: Windows directory junctions
        // exercise BCL reparse-point checks without granting symbolic-link privileges.
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        start.Environment["COGITIDE_WORKTREE_TEST_LINK"] = link;
        start.Environment["COGITIDE_WORKTREE_TEST_TARGET"] = target;
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "New-Item -ItemType Junction -Path $env:COGITIDE_WORKTREE_TEST_LINK -Target $env:COGITIDE_WORKTREE_TEST_TARGET -ErrorAction Stop | Out-Null" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(process.ExitCode == 0, await error);
    }

    private async Task Initialize()
    {
        await Git(Repository, "init");
        await Git(Repository, "config", "user.name", "Deterministic fixture");
        await Git(Repository, "config", "user.email", "fixture@example.invalid");
        await File.WriteAllTextAsync(Path.Combine(Repository, "source.txt"), "initial");
        await File.WriteAllTextAsync(Path.Combine(Repository, ".gitignore"), "ignored.txt\n");
        await Git(Repository, "add", "source.txt", ".gitignore");
        await Git(Repository, "-c", "commit.gpgsign=false", "commit", "-m", "fixture");
    }
    private async Task Git(string workspace, params string[] arguments)
    {
        var result = await _service.RunProgramAsync(workspace, "git", arguments, 20);
        Assert.False(result.IsError, result.Content);
    }
    private static JsonElement Json(ToolResult result) { using var document = JsonDocument.Parse(result.Content); return document.RootElement.Clone(); }
    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(parent, comparison) || !Path.GetFileName(full).StartsWith("cogitide-worktrees-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fixture cleanup path.");
        if (!Directory.Exists(full)) return;
        foreach (var file in Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(full, true);
    }
}
