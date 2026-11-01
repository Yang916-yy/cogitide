using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Workbench.Protocol;

namespace Workbench.Tools;

public sealed partial class WorkspaceService
{
    private const int ReviewOutputLimit = 2_000_000;
    private const long ReviewByteLimit = 32L * 1024 * 1024;
    private static readonly GitReviewDiff EmptyReviewDiff = new("", [], false, false);
    // This recognizes only Git's own hunk delimiters. Payload, path quoting, binary data,
    // context matching and application remain Git's responsibility.
    private static readonly Regex ReviewHunkHeader = new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@[^\n]*\n",
        RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public async Task<GitReviewSnapshot> ReviewAsync(string workspace, CancellationToken ct = default)
    {
        var (scope, repository) = await ReviewRepositoryAsync(workspace, ct);
        return await ReadReviewAsync(scope, repository, ct);
    }

    private static async Task<(string Scope, string Repository)> ReviewRepositoryAsync(string workspace, CancellationToken ct)
    {
        ValidateWorktreePathText(workspace);
        var scope = Root(NormalizeWorktreePath(workspace));
        CheckPhysicalPath(scope);
        var repository = await WorktreeGitDirectoryAsync(scope, "--show-toplevel", ct);
        CheckPhysicalPath(repository);
        if (!scope.Equals(repository, PathComparison) && !scope.StartsWith(repository + Path.DirectorySeparatorChar, PathComparison))
            throw new UnauthorizedAccessException("Select a physical directory inside this Git checkout, outside Git internals.");
        // Linked-worktree metadata may be elsewhere, but must still be a physical path.
        CheckPhysicalPath(await WorktreeGitDirectoryAsync(scope, "--git-common-dir", ct));
        return (scope, repository);
    }

    private static async Task<string> ReviewReadGitAsync(string root, string[] arguments, CancellationToken ct, int maximum = ReviewOutputLimit,
        bool allowDifferenceExit = false)
    {
        var result = await ReadGitAsync(root,
            ["--no-replace-objects", "--no-optional-locks", "--literal-pathspecs", "-c", "core.hooksPath=/dev/null", .. arguments], maximum, ct);
        if (result.Truncated) throw new InvalidOperationException("The review exceeds its output budget. Select a smaller directory or inspect this change in Git.");
        if (result.ExitCode != 0 && !(allowDifferenceExit && result.ExitCode == 1))
            throw new InvalidOperationException("Git could not read this review. Resolve repository access or conflicts and refresh.");
        return result.Output;
    }

    private sealed record ReviewObservation(string Status, string Staged, string Branch, string? Head, string PublicationConfig);

    private static async Task<ReviewObservation> ReviewObservationAsync(string scope, string repository, CancellationToken ct)
    {
        var status = await ReviewReadGitAsync(scope, ["status", "--porcelain=v1", "-z", "--untracked-files=all", "--no-renames", "--", "."], ct);
        // The whole index is included, even for a selected subtree. A foreign staged change
        // invalidates approval of a commit rather than silently joining it.
        var staged = await ReviewReadGitAsync(repository, ["diff", "--cached", "--raw", "--full-index", "--no-abbrev", "--no-renames", "-z", "--no-ext-diff", "--no-textconv", "--"], ct);
        var headResult = await ReadGitAsync(repository, ["--no-replace-objects", "rev-parse", "--verify", "HEAD"], 4096, ct);
        if (headResult.Truncated) throw new InvalidDataException("Git returned an incomplete HEAD.");
        var head = headResult.ExitCode == 0 ? headResult.Output.Trim() : null;
        var branchResult = await ReadGitAsync(repository, ["symbolic-ref", "--quiet", "--short", "HEAD"], 4096, ct);
        if (branchResult.Truncated) throw new InvalidDataException("Git returned an incomplete branch.");
        // Bind publication to the same remote/upstream configuration the user observed. Values
        // can contain authenticated URLs, so they are hashed only and never returned in DTOs.
        var publicationConfig = await ReviewReadGitAsync(repository,
            ["config", "--null", "--get-regexp", @"^(remote\..*\.(url|pushurl|push)|branch\..*\.(remote|pushremote|merge|gh-merge-base)|push\.default|remote\.pushdefault)$"], ct, 65536, allowDifferenceExit: true);
        return new(status, staged, branchResult.ExitCode == 0 ? branchResult.Output.Trim() : "Detached HEAD", head, publicationConfig);
    }

    private static async Task<GitReviewSnapshot> ReadReviewAsync(string scope, string repository, CancellationToken ct)
    {
        var observation = await ReviewObservationAsync(scope, repository, ct);
        var fields = observation.Status.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length > 200) throw new InvalidOperationException("More than 200 paths changed. Select a smaller directory for review.");
        if (fields.Any(field => field.Length < 4 || field[2] != ' ')) throw new InvalidDataException("Git returned an invalid review path record.");
        List<GitReviewFile> files = [];
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Fingerprint(string value) => fingerprint.AppendData(Encoding.UTF8.GetBytes(value + "\0"));
        Fingerprint(scope); Fingerprint(repository); Fingerprint(observation.Status); Fingerprint(observation.Staged);
        Fingerprint(observation.Branch); Fingerprint(observation.Head ?? "unborn"); Fingerprint(observation.PublicationConfig);
        var totalPatch = 0;
        long totalBytes = 0;
        // A staged deletion followed by a newly created file at the same path appears twice
        // in porcelain v1: `D  path` and `?? path`. Keep both areas under one file identity.
        foreach (var group in fields.GroupBy(field => field[3..], StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var field = group.FirstOrDefault(item => !item.StartsWith("??", StringComparison.Ordinal)) ?? group.First();
            // Porcelain v1 always names paths from the checkout root, irrespective of cwd.
            var path = Path.GetRelativePath(scope, Resolve(repository, field[3..])).Replace(Path.DirectorySeparatorChar, '/');
            var indexStatus = field[..1];
            var untracked = group.Any(item => item.StartsWith("??", StringComparison.Ordinal));
            var worktreeStatus = untracked ? "?" : field.Substring(1, 1);
            var conflict = indexStatus == "U" || worktreeStatus == "U" || field[..2] is "AA" or "DD";
            string? unavailable = null;
            string? full = null;
            try
            {
                ValidateReviewPath(path);
                full = Resolve(scope, path);
                CheckPhysicalPath(full);
                if (Directory.Exists(full)) unavailable = "Directories and submodules require inspection in Git.";
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
            { unavailable = "This path is linked, outside the selected directory, or unsupported. Inspect it in Git."; }
            if (full is not null && unavailable is null && File.Exists(full))
            {
                await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                totalBytes += stream.Length;
                if (totalBytes > ReviewByteLimit) throw new InvalidOperationException("Changed files exceed the 32 MiB review budget. Select a smaller directory.");
                Fingerprint(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)));
            }
            else Fingerprint(unavailable ?? "missing");
            var index = EmptyReviewDiff; var worktree = EmptyReviewDiff;
            if (unavailable is null && !conflict)
            {
                if (indexStatus is not (" " or "?")) index = await ReadReviewDiffAsync(scope, path, true, ct);
                if (untracked)
                {
                    var patch = await ReviewReadGitAsync(scope,
                        ["diff", "--no-index", "--no-ext-diff", "--no-textconv", "--no-color", "--src-prefix=a/", "--dst-prefix=b/",
                            "--output-indicator-new=+", "--output-indicator-old=-", "--output-indicator-context= ", "--", "/dev/null", path], ct, 256_000, allowDifferenceExit: true);
                    worktree = FrameReviewDiff(patch, false);
                }
                else if (worktreeStatus != " ") worktree = await ReadReviewDiffAsync(scope, path, false, ct);
                totalPatch += index.Patch.Length + worktree.Patch.Length;
                if (totalPatch > 1_000_000) throw new InvalidOperationException("Review patches exceed 1,000,000 characters. Select a smaller directory.");
            }
            if (conflict) unavailable = "Resolve merge conflicts in Git before staging or committing from review.";
            Fingerprint(index.Patch); Fingerprint(worktree.Patch);
            files.Add(new(path, indexStatus, worktreeStatus, index, worktree, untracked, conflict, unavailable));
        }
        if (observation != await ReviewObservationAsync(scope, repository, ct))
            throw new InvalidOperationException("The repository changed while reading the review. Refresh before acting.");
        var isRoot = scope.Equals(repository, PathComparison);
        var warning = !isRoot ? "Review is scoped to this directory. Select the repository root to commit or publish the entire index."
            : files.Count == 0 ? "Working tree is clean. Committed changes can still be pushed or proposed as a pull request." : null;
        return new(Convert.ToHexStringLower(fingerprint.GetHashAndReset()), repository, scope, observation.Branch,
            observation.Head, files, isRoot && !files.Any(file => file.IsConflict || file.UnavailableReason is not null), warning);
    }

    private static async Task<GitReviewDiff> ReadReviewDiffAsync(string scope, string path, bool staged, CancellationToken ct)
    {
        var patch = await ReviewReadGitAsync(scope,
            ["diff", .. staged ? new[] { "--cached" } : [], "--relative", "--no-renames", "--no-ext-diff", "--no-textconv", "--no-color",
             "--src-prefix=a/", "--dst-prefix=b/", "--output-indicator-new=+", "--output-indicator-old=-", "--output-indicator-context= ", "--unified=3", "--", path], ct, 256_000);
        return FrameReviewDiff(patch, true);
    }

    private static GitReviewDiff FrameReviewDiff(string patch, bool allowHunks)
    {
        if (patch.Length == 0) return EmptyReviewDiff;
        var binary = patch.Contains("\nBinary files ", StringComparison.Ordinal) || patch.Contains("\nGIT binary patch\n", StringComparison.Ordinal);
        var matches = ReviewHunkHeader.Matches(patch);
        var selectable = allowHunks && matches.Count > 0 && !binary &&
            !patch.Contains("\nnew file mode ", StringComparison.Ordinal) && !patch.Contains("\ndeleted file mode ", StringComparison.Ordinal) &&
            !patch.Contains("\nold mode ", StringComparison.Ordinal) && !patch.Contains("\nnew mode ", StringComparison.Ordinal);
        List<GitReviewHunk> hunks = [];
        if (matches.Count > 0)
        {
            var prefix = patch[..matches[0].Index];
            for (var i = 0; i < matches.Count; i++)
            {
                var match = matches[i]; var end = i + 1 < matches.Count ? matches[i + 1].Index : patch.Length;
                var opaque = prefix + patch[match.Index..end];
                int Number(int n, int fallback = 1) => match.Groups[n].Success ? int.Parse(match.Groups[n].Value, CultureInfo.InvariantCulture) : fallback;
                hunks.Add(new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(opaque))), match.Value.TrimEnd('\n', '\r'),
                    opaque, Number(1), Number(2), Number(3), Number(4)));
            }
        }
        return new(patch, hunks, selectable, true, binary);
    }

    private static void ValidateReviewPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || path.Split('/').Any(part => part is "." or ".."))
            throw new UnauthorizedAccessException("Unsupported review path.");
    }
}
