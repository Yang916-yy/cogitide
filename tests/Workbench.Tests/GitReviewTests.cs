using System.Diagnostics;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

[Collection("Managed worktree Git environment")]
public sealed class GitReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(PhysicalTemporaryDirectory(), "cogitide-review-" + Guid.NewGuid().ToString("N"));
    private readonly WorkspaceService _service = new();
    private string Repo => Path.Combine(_root, "repository with spaces");
    public GitReviewTests() => Directory.CreateDirectory(Repo);

    [Fact]
    public async Task HunkStageUnstageAndDiscardPreserveOtherStagedAndWorkingChangesInSameFile()
    {
        await Initialize();
        var lines = Lines(); lines[0] = "staged first line";
        await File.WriteAllTextAsync(Path.Combine(Repo, "source.txt"), string.Join('\n', lines) + "\n");
        await Git(Repo, "add", "source.txt");
        lines[20] = "working middle line"; lines[50] = "working final line";
        var working = string.Join('\n', lines) + "\n";
        await File.WriteAllTextAsync(Path.Combine(Repo, "source.txt"), working);
        await File.WriteAllTextAsync(Path.Combine(Repo, "other.txt"), "unrelated staged file\n");
        await Git(Repo, "add", "other.txt");
        var review = await _service.ReviewAsync(Repo);
        var file = review.Files.Single(item => item.Path == "source.txt");
        Assert.True(file.Index.HasChanges); Assert.True(file.Worktree.CanSelectHunks);
        Assert.Equal(2, file.Worktree.Hunks.Count);
        review = await _service.ChangeReviewAsync(Repo, review.Snapshot, file.Path, "worktree", "stage", file.Worktree.Hunks[0].Id);
        Assert.Equal(working, await File.ReadAllTextAsync(Path.Combine(Repo, "source.txt")));
        var staged = await Git(Repo, "show", ":source.txt");
        Assert.Contains("staged first line", staged); Assert.Contains("working middle line", staged); Assert.DoesNotContain("working final line", staged);
        Assert.Contains("other.txt", await Git(Repo, "diff", "--cached", "--name-only"));
        file = review.Files.Single(item => item.Path == "source.txt");
        var middle = file.Index.Hunks.Single(hunk => hunk.Patch.Contains("+working middle line", StringComparison.Ordinal));
        review = await _service.ChangeReviewAsync(Repo, review.Snapshot, file.Path, "index", "unstage", middle.Id);
        Assert.Equal(working, await File.ReadAllTextAsync(Path.Combine(Repo, "source.txt")));
        file = review.Files.Single(item => item.Path == "source.txt");
        var discard = file.Worktree.Hunks.Single(hunk => hunk.Patch.Contains("+working middle line", StringComparison.Ordinal));
        review = await _service.ChangeReviewAsync(Repo, review.Snapshot, file.Path, "worktree", "revert", discard.Id, confirmed: true);
        var remaining = await File.ReadAllTextAsync(Path.Combine(Repo, "source.txt"));
        Assert.Contains("staged first line", remaining); Assert.Contains("working final line", remaining); Assert.DoesNotContain("working middle line", remaining);
        Assert.Contains("staged first line", await Git(Repo, "show", ":source.txt"));
        Assert.Contains("other.txt", await Git(Repo, "diff", "--cached", "--name-only"));
        Assert.Single(review.Files.Single(item => item.Path == "source.txt").Worktree.Hunks);
    }

    [Fact]
    public async Task StaleBytesOrGlobalIndexInvalidateActionsWithoutLosingChanges()
    {
        await Initialize();
        await File.WriteAllTextAsync(Path.Combine(Repo, "source.txt"), "first edit\n");
        var review = await _service.ReviewAsync(Repo);
        await File.WriteAllTextAsync(Path.Combine(Repo, "source.txt"), "second edit\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ChangeReviewAsync(Repo, review.Snapshot, "source.txt", "worktree", "stage"));
        Assert.Empty(await Git(Repo, "diff", "--cached", "--name-only"));
        Assert.Equal("second edit\n", await File.ReadAllTextAsync(Path.Combine(Repo, "source.txt")));
        review = await _service.ReviewAsync(Repo);
        await File.WriteAllTextAsync(Path.Combine(Repo, "other.txt"), "user staged after review\n"); await Git(Repo, "add", "other.txt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ChangeReviewAsync(Repo, review.Snapshot, "source.txt", "worktree", "stage"));
        Assert.Equal("other.txt", (await Git(Repo, "diff", "--cached", "--name-only")).Trim());
    }

    [Fact]
    public async Task UntrackedUnicodePathRequiresExplicitDiscardAndNeverTouchesIgnoredSibling()
    {
        await Initialize();
        const string path = "new 文件 with spaces.txt";
        await File.WriteAllTextAsync(Path.Combine(Repo, path), "new content\n");
        await File.WriteAllTextAsync(Path.Combine(Repo, "ignored.txt"), "keep ignored content\n");
        var review = await _service.ReviewAsync(Repo);
        var file = review.Files.Single(item => item.Path == path);
        Assert.True(file.IsUntracked); Assert.Contains("+new content", file.Worktree.Patch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.ChangeReviewAsync(Repo, review.Snapshot, path, "worktree", "revert"));
        review = await _service.ChangeReviewAsync(Repo, review.Snapshot, path, "worktree", "stage");
        review = await _service.ChangeReviewAsync(Repo, review.Snapshot, path, "index", "unstage");
        Assert.True(review.Files.Single(item => item.Path == path).IsUntracked);
        await _service.ChangeReviewAsync(Repo, review.Snapshot, path, "worktree", "revert", confirmed: true);
        Assert.False(File.Exists(Path.Combine(Repo, path)));
        Assert.Equal("keep ignored content\n", await File.ReadAllTextAsync(Path.Combine(Repo, "ignored.txt")));
    }

    [Fact]
    public async Task StagedDeletionAndUntrackedReplacementShareOneIdentityWithoutDiscardingTheReplacement()
    {
        await Initialize();
        await Git(Repo, "rm", "source.txt");
        await File.WriteAllTextAsync(Path.Combine(Repo, "source.txt"), "replacement not staged\n");
        var review = await _service.ReviewAsync(Repo);
        var file = Assert.Single(review.Files);
        Assert.True(file.IsUntracked); Assert.Equal("D", file.IndexStatus);
        Assert.True(file.Index.HasChanges); Assert.True(file.Worktree.HasChanges);
        review = await _service.ChangeReviewAsync(Repo, review.Snapshot, "source.txt", "index", "unstage");
        Assert.Equal("replacement not staged\n", await File.ReadAllTextAsync(Path.Combine(Repo, "source.txt")));
        Assert.Empty(await Git(Repo, "diff", "--cached", "--name-only"));
        Assert.True(Assert.Single(review.Files).Worktree.HasChanges);
    }

    [Fact]
    public async Task RenameCommitIncludesOnlyReviewedIndexWhileWorkingEditRemains()
    {
        await Initialize();
        await Git(Repo, "mv", "source.txt", "renamed source.txt");
        await File.AppendAllTextAsync(Path.Combine(Repo, "renamed source.txt"), "working edit after rename\n");
        var review = await _service.ReviewAsync(Repo);
        Assert.Contains(review.Files, item => item.Path == "source.txt" && item.IndexStatus == "D");
        Assert.Contains(review.Files, item => item.Path == "renamed source.txt" && item.IndexStatus == "A" && item.Worktree.HasChanges);
        var result = await _service.CommitReviewAsync(Repo, review.Snapshot, "Review rename\n\nKeep working edit outside commit.");
        Assert.Equal(result.Commit, (await Git(Repo, "rev-parse", "HEAD")).Trim());
        Assert.DoesNotContain("working edit after rename", await Git(Repo, "show", "HEAD:renamed source.txt"));
        Assert.Contains("working edit after rename", await File.ReadAllTextAsync(Path.Combine(Repo, "renamed source.txt")));
        Assert.Empty(await Git(Repo, "diff", "--cached", "--name-only"));
        Assert.Contains(result.Review.Files, item => item.Path == "renamed source.txt" && item.Worktree.HasChanges);
    }

    [Fact]
    public async Task SelectedSubdirectoryUsesRelativePathsAndCannotCommitStagingOutsideItsScope()
    {
        await Initialize();
        var scope = Path.Combine(Repo, "scope"); Directory.CreateDirectory(scope);
        await File.WriteAllTextAsync(Path.Combine(scope, "inside.txt"), "inside\n");
        await File.WriteAllTextAsync(Path.Combine(Repo, "outside.txt"), "staged outside\n"); await Git(Repo, "add", "outside.txt");
        var review = await _service.ReviewAsync(scope);
        Assert.False(review.CanCommit); Assert.Equal("inside.txt", Assert.Single(review.Files).Path);
        review = await _service.ChangeReviewAsync(scope, review.Snapshot, "inside.txt", "worktree", "stage");
        Assert.Contains("scope/inside.txt", await Git(Repo, "diff", "--cached", "--name-only"));
        Assert.Contains("outside.txt", await Git(Repo, "diff", "--cached", "--name-only"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CommitReviewAsync(scope, review.Snapshot, "must not commit outside scope"));
        await File.WriteAllTextAsync(Path.Combine(scope, "inside.txt"), "modified inside\n");
        review = await _service.ReviewAsync(scope);
        var hunk = Assert.Single(review.Files.Single(file => file.Path == "inside.txt").Worktree.Hunks);
        await _service.ChangeReviewAsync(scope, review.Snapshot, "inside.txt", "worktree", "stage", hunk.Id);
        Assert.Equal("modified inside", (await Git(Repo, "show", ":scope/inside.txt")).Trim());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ChangeReviewAsync(scope, review.Snapshot, "../outside.txt", "index", "unstage"));
    }

    [Fact]
    public async Task AmbientGitRepositoryIndexAndConfigInjectionCannotRedirectReviewOrStage()
    {
        await Initialize();
        var other = Path.Combine(_root, "foreign"); Directory.CreateDirectory(other); await Initialize(other);
        var originalIndex = await File.ReadAllBytesAsync(Path.Combine(other, ".git", "index"));
        await File.WriteAllTextAsync(Path.Combine(Repo, "source.txt"), "authorized repository change\n");
        var poisoned = new Dictionary<string, string?>
        {
            ["GIT_DIR"] = Path.Combine(other, ".git"), ["GIT_WORK_TREE"] = other,
            ["GIT_INDEX_FILE"] = Path.Combine(other, ".git", "index"), ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_CONFIG_KEY_0"] = "core.worktree", ["GIT_CONFIG_VALUE_0"] = other
        };
        var before = poisoned.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var item in poisoned) Environment.SetEnvironmentVariable(item.Key, item.Value);
            var review = await _service.ReviewAsync(Repo);
            await _service.ChangeReviewAsync(Repo, review.Snapshot, "source.txt", "worktree", "stage");
        }
        finally { foreach (var item in before) Environment.SetEnvironmentVariable(item.Key, item.Value); }
        Assert.Equal(originalIndex, await File.ReadAllBytesAsync(Path.Combine(other, ".git", "index")));
        Assert.Contains("authorized repository change", await Git(Repo, "show", ":source.txt"));
        Assert.Equal(string.Join('\n', Lines()) + "\n", await File.ReadAllTextAsync(Path.Combine(other, "source.txt")));
    }

    [Fact]
    public async Task LinkedAncestorIsRejectedBeforeAccessingForeignCheckout()
    {
        await Initialize();
        var link = Path.Combine(_root, "linked");
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.Environment["COGITIDE_REVIEW_LINK"] = link; start.Environment["COGITIDE_REVIEW_TARGET"] = Repo;
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "New-Item -ItemType Junction -Path $env:COGITIDE_REVIEW_LINK -Target $env:COGITIDE_REVIEW_TARGET -ErrorAction Stop | Out-Null" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0, process.ExitCode);
        }
        else Directory.CreateSymbolicLink(link, Repo);
        try
        {
            Directory.CreateDirectory(Path.Combine(Repo, "nested"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.ReviewAsync(Path.Combine(link, "nested")));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task LocalPushRequiresAnExplicitActionAndNeverStagesWorkingChanges()
    {
        await Initialize();
        var bare = Path.Combine(_root, "local-remote.git"); Directory.CreateDirectory(bare); await Git(bare, "init", "--bare");
        await Git(Repo, "remote", "add", "origin", bare);
        var branch = (await Git(Repo, "branch", "--show-current")).Trim();
        await Git(Repo, "push", "--set-upstream", "origin", branch);
        await File.WriteAllTextAsync(Path.Combine(Repo, "source.txt"), "staged commit\n"); await Git(Repo, "add", "source.txt");
        var committed = await _service.CommitReviewAsync(Repo, (await _service.ReviewAsync(Repo)).Snapshot, "local push fixture");
        await File.WriteAllTextAsync(Path.Combine(Repo, "source.txt"), "uncommitted working bytes\n");
        var review = await _service.ReviewAsync(Repo);
        Assert.NotEqual(committed.Commit, (await Git(bare, "rev-parse", "refs/heads/" + branch)).Trim());
        var published = await _service.PublishReviewAsync(Repo, review.Snapshot, "push");
        Assert.Equal("push", published.Action); Assert.Null(published.Url);
        Assert.Equal(committed.Commit, (await Git(bare, "rev-parse", "refs/heads/" + branch)).Trim());
        Assert.Equal("uncommitted working bytes\n", await File.ReadAllTextAsync(Path.Combine(Repo, "source.txt")));
        Assert.Empty(await Git(Repo, "diff", "--cached", "--name-only"));
    }

    private static string[] Lines() => Enumerable.Range(1, 65).Select(number => $"original line {number:00}").ToArray();
    private async Task Initialize(string? repository = null)
    {
        repository ??= Repo;
        await Git(repository, "init"); await Git(repository, "config", "user.name", "Review fixture");
        await Git(repository, "config", "user.email", "fixture@example.invalid"); await Git(repository, "config", "core.autocrlf", "false");
        await File.WriteAllTextAsync(Path.Combine(repository, "source.txt"), string.Join('\n', Lines()) + "\n");
        await File.WriteAllTextAsync(Path.Combine(repository, ".gitignore"), "ignored.txt\n");
        await Git(repository, "add", "."); await Git(repository, "-c", "commit.gpgsign=false", "commit", "-m", "fixture");
    }
    private static async Task<string> Git(string root, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(process.ExitCode == 0, await error); return await output;
    }
    private static string PhysicalTemporaryDirectory()
    {
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (OperatingSystem.IsMacOS() && temp.StartsWith("/var/", StringComparison.Ordinal)) return "/private" + temp;
        return temp;
    }
    public void Dispose()
    {
        var full = Path.GetFullPath(_root);
        var temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(PhysicalTemporaryDirectory())) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !Path.GetFileName(full).StartsWith("cogitide-review-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected review fixture cleanup target.");
        if (!Directory.Exists(full)) return;
        foreach (var file in Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        Directory.Delete(full, true);
    }
}
