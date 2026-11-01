namespace Workbench.Protocol;

/// <summary>A bounded observation of one physical checkout, tied to HEAD, the entire index and changed file bytes.</summary>
public sealed record GitReviewSnapshot(string Snapshot, string RepositoryRoot, string WorkspacePath, string Branch,
    string? HeadCommit, IReadOnlyList<GitReviewFile> Files, bool CanCommit, string? Warning = null);

public sealed record GitReviewFile(string Path, string IndexStatus, string WorktreeStatus,
    GitReviewDiff Index, GitReviewDiff Worktree, bool IsUntracked = false, bool IsConflict = false,
    string? UnavailableReason = null)
{
    public string DisplayName => $"{IndexStatus}{WorktreeStatus}  {Path}";
}

/// <summary>Patch text comes from Git. Hunk patches are opaque Git-generated fragments, never client supplied.</summary>
public sealed record GitReviewDiff(string Patch, IReadOnlyList<GitReviewHunk> Hunks, bool CanSelectHunks,
    bool HasChanges, bool IsBinary = false);
public sealed record GitReviewHunk(string Id, string Header, string Patch, int OldStart, int OldLines, int NewStart, int NewLines);
public sealed record GitReviewCommitResult(string Commit, GitReviewSnapshot Review);
public sealed record GitReviewPublishResult(string Action, string? Url, string Message, GitReviewSnapshot Review);
public sealed record GitReviewFeedback(string Snapshot, string Path, string Area, string? HunkHeader, int? Line, string Text);
