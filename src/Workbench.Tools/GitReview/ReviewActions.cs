using System.Text;
using Workbench.Protocol;

namespace Workbench.Tools;

public sealed partial class WorkspaceService
{
    public async Task<GitReviewSnapshot> ChangeReviewAsync(string workspace, string expectedSnapshot, string path, string area,
        string action, string? hunkId = null, bool confirmed = false, CancellationToken ct = default)
    {
        if (area is not ("worktree" or "index") || action is not ("stage" or "unstage" or "revert"))
            throw new ArgumentException("Choose a supported review area and action.");
        if (action == "stage" && area != "worktree" || action == "unstage" && area != "index" || action == "revert" && area != "worktree")
            throw new ArgumentException("Stage working changes, unstage staged changes, or revert working changes.");
        if (action == "revert" && !confirmed) throw new InvalidOperationException("Confirm the selected working change before discarding it.");
        ValidateReviewPath(path);
        var (scope, repository) = await ReviewRepositoryAsync(workspace, ct);
        var full = Resolve(scope, path);
        var repositoryGate = FileLocks.GetOrAdd(repository, _ => new SemaphoreSlim(1, 1));
        var fileGate = FileLocks.GetOrAdd(full, _ => new SemaphoreSlim(1, 1));
        await repositoryGate.WaitAsync(ct);
        try
        {
            await fileGate.WaitAsync(ct);
            try
            {
                var review = await RequireReviewSnapshotAsync(scope, repository, expectedSnapshot, ct);
                var file = review.Files.SingleOrDefault(item => item.Path == path)
                    ?? throw new InvalidOperationException("The selected change is no longer in this review. Refresh.");
                if (file.UnavailableReason is not null) throw new InvalidOperationException(file.UnavailableReason);
                CheckPhysicalPath(full);
                var diff = area == "index" ? file.Index : file.Worktree;
                if (!diff.HasChanges) throw new InvalidOperationException("No changes exist in this review area.");
                if (hunkId is not null)
                {
                    if (!diff.CanSelectHunks) throw new InvalidOperationException("This file requires a whole-file action.");
                    var hunk = diff.Hunks.SingleOrDefault(item => item.Id == hunkId)
                        ?? throw new InvalidOperationException("The selected hunk changed. Refresh before acting.");
                    // Git validates the exact generated fragment twice. No fuzz, whitespace repair,
                    // reject files or automatic fallback to a whole-file operation is permitted.
                    string[] options = action switch
                    {
                        "stage" => ["--cached"],
                        "unstage" => ["--cached", "--reverse"],
                        _ => ["--reverse"]
                    };
                    // apply interprets paths from the repository root and otherwise silently
                    // skips a --relative patch from a subdirectory, sometimes with exit 0.
                    var prefix = Path.GetRelativePath(repository, scope).Replace(Path.DirectorySeparatorChar, '/');
                    string[] directory = prefix == "." ? [] : ["--directory=" + prefix];
                    await ReviewMutateGitAsync(repository, ["apply", "--check", "--whitespace=nowarn", .. options, .. directory, "-"], ct, hunk.Patch);
                    await RequireReviewSnapshotAsync(scope, repository, expectedSnapshot, ct);
                    await ReviewMutateGitAsync(repository, ["apply", "--whitespace=nowarn", .. options, .. directory, "-"], ct, hunk.Patch);
                }
                else if (action == "stage")
                    await ReviewMutateGitAsync(scope, ["add", "--", path], ct);
                else if (action == "unstage")
                {
                    if (review.HeadCommit is null) await ReviewMutateGitAsync(scope, ["rm", "--cached", "--", path], ct);
                    else await ReviewMutateGitAsync(scope, ["restore", "--staged", "--source=HEAD", "--", path], ct);
                }
                else if (file.IsUntracked)
                {
                    // One checked regular file only; never recurse or touch ignored siblings.
                    Resolve(scope, path); CheckPhysicalPath(full);
                    ct.ThrowIfCancellationRequested();
                    File.Delete(full);
                }
                else
                    // Source is the current index, preserving any staged work in the same file.
                    await ReviewMutateGitAsync(scope, ["restore", "--worktree", "--", path], ct);
                return await ReviewAfterMutationAsync(scope, repository, ct);
            }
            finally { fileGate.Release(); }
        }
        finally { repositoryGate.Release(); }
    }

    public async Task<GitReviewCommitResult> CommitReviewAsync(string workspace, string expectedSnapshot, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 20_000 || message.Contains('\0'))
            throw new ArgumentException("Enter a commit message of at most 20,000 characters.");
        var (scope, repository) = await ReviewRepositoryAsync(workspace, ct);
        var gate = FileLocks.GetOrAdd(repository, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var review = await RequireReviewSnapshotAsync(scope, repository, expectedSnapshot, ct);
            if (!review.CanCommit) throw new InvalidOperationException("Select the repository root and resolve unavailable paths or conflicts before committing.");
            if (!review.Files.Any(file => file.Index.HasChanges)) throw new InvalidOperationException("Stage the changes to include in this commit first.");
            // No `-a` or paths: commit precisely the index that the user reviewed.
            await ReviewMutateGitAsync(repository, ["-c", "commit.gpgSign=false", "commit", "--no-verify", "--cleanup=strip", "-F", "-"], ct, message);
            var after = await ReviewAfterMutationAsync(scope, repository, ct);
            return new(after.HeadCommit ?? throw new InvalidDataException("Commit outcome is unknown. Refresh Git before retrying."), after);
        }
        finally { gate.Release(); }
    }

    public async Task<GitReviewPublishResult> PublishReviewAsync(string workspace, string expectedSnapshot, string action,
        string? title = null, string? body = null, string? baseBranch = null, CancellationToken ct = default)
    {
        if (action is not ("push" or "create_pr")) throw new ArgumentException("Choose push or create_pr.");
        if (title?.Length > 512 || body?.Length > 40_000 || title?.Contains('\0') == true || body?.Contains('\0') == true)
            throw new ArgumentException("Pull request title or description exceeds its supported length.");
        if (action == "create_pr" && string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Enter a pull request title.");
        if (baseBranch is { Length: > 256 } || baseBranch?.Any(char.IsControl) == true || baseBranch?.StartsWith('-') == true)
            throw new ArgumentException("Choose a valid base branch name.");
        var (scope, repository) = await ReviewRepositoryAsync(workspace, ct);
        var gate = FileLocks.GetOrAdd(repository, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var review = await RequireReviewSnapshotAsync(scope, repository, expectedSnapshot, ct);
            if (!scope.Equals(repository, PathComparison) || review.HeadCommit is null || review.Branch == "Detached HEAD")
                throw new InvalidOperationException("Select the repository root and a committed local branch before publishing.");
            if (action == "push")
            {
                // Plain push honors the user's configured upstream; never guess a remote,
                // set upstream, force push, or change configuration in this workflow.
                await ReviewMutateGitAsync(repository, ["push"], ct, timeout: 90);
                return new(action, null, "Push completed for the configured upstream.", await ReviewAfterMutationAsync(scope, repository, ct));
            }
            // --head prevents gh from automatically pushing an unpublished branch. Publication
            // remains a separate explicit button. Existing gh credential storage owns auth.
            var args = new List<string> { "pr", "create", "--head", review.Branch, "--title", title!.Trim(), "--body-file", "-" };
            if (!string.IsNullOrWhiteSpace(baseBranch)) { args.Add("--base"); args.Add(baseBranch); }
            var output = await ReviewCommandAsync("gh", repository, args.ToArray(), ct, body ?? "", 90);
            // Return only a validated public PR URL, never raw CLI output, tokens or remote URLs.
            var url = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim()).LastOrDefault(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                    uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) &&
                    System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath, @"^/[^/]+/[^/]+/pull/\d+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant));
            return new(action, url, url is null ? "Pull request command completed. Inspect the remote repository to confirm its URL." : "Pull request created.",
                await ReviewAfterMutationAsync(scope, repository, ct));
        }
        finally { gate.Release(); }
    }

    private static async Task<GitReviewSnapshot> RequireReviewSnapshotAsync(string scope, string repository, string expected, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expected)) throw new ArgumentException("Refresh the review before acting.");
        var current = await ReadReviewAsync(scope, repository, ct);
        if (!current.Snapshot.Equals(expected, StringComparison.Ordinal))
            throw new InvalidOperationException("The repository changed since this review. Refresh and review the current changes before retrying.");
        return current;
    }

    private static async Task<GitReviewSnapshot> ReviewAfterMutationAsync(string scope, string repository, CancellationToken ct)
    {
        try { return await ReadReviewAsync(scope, repository, ct); }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The operation command completed, but its updated repository state could not be observed. Inspect Git or the remote and refresh before retrying.", ex);
        }
    }

    private static Task<string> ReviewMutateGitAsync(string root, string[] args, CancellationToken ct, string? input = null, int timeout = 30) =>
        ReviewCommandAsync("git", root, ["--no-pager", "--no-replace-objects", "--literal-pathspecs", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null", .. args], ct, input, timeout);

    private static async Task<string> ReviewCommandAsync(string executable, string root, string[] args, CancellationToken ct, string? input = null, int timeout = 30)
    {
        Task inputTask = Task.CompletedTask;
        async Task WriteInputAsync(Stream stream)
        {
            try { await stream.WriteAsync(Encoding.UTF8.GetBytes(input!), ct); await stream.FlushAsync(ct); }
            finally { stream.Dispose(); }
        }
        // gh needs its own standard auth variables, but must not inherit an alternate Git index,
        // repository, config injection or unrelated model keys through a child Git process.
        var excluded = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("GH_REPO", StringComparison.OrdinalIgnoreCase) || name.Equals("GH_HOST", StringComparison.OrdinalIgnoreCase)).ToArray();
        ProcessResult result;
        try
        {
            result = await ExecuteAsync(executable, args, root, timeout, null, ct, excluded,
                input is null ? null : stream => inputTask = WriteInputAsync(stream));
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException("The Git operation was cancelled before a complete result was observed. Its outcome is unknown; inspect Git or the remote before retrying.", ex);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(executable == "gh"
                ? "Install GitHub CLI on the selected Host, run gh auth login there, then refresh this review. No pull request was created by this command."
                : "Install Git on the selected Host and make it available on PATH, then refresh this review.", ex);
        }
        finally
        {
            try { await inputTask; }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        }
        if (result.TimedOut || result.Truncated)
            throw new InvalidOperationException("The Git operation stopped before a complete result was observed. Its outcome is unknown; refresh Git or inspect the remote before retrying.");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(executable == "gh"
                ? "GitHub CLI could not create the pull request. Check gh installation/authentication and that this branch is already pushed; inspect the remote before retrying."
                : "Git did not complete the operation. Check repository access, configured author/upstream, or patch conflicts, then refresh before retrying.");
        return result.Output;
    }
}
