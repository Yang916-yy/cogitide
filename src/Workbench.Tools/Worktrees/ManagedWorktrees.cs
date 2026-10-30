using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Workbench.Protocol;

namespace Workbench.Tools;

internal sealed record ManagedWorktree(string Id, string SessionId, string Repository, string Path, string Branch,
    string StartCommit, DateTimeOffset CreatedAt);

/// <summary>Git owns checkout, index, locking and dirty-removal checks. This adapter owns only managed-path metadata.</summary>
public sealed partial class WorkspaceService
{
    private static StringComparer WorktreePathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WorktreeLocks = new(WorktreePathComparer);

    public async Task<ToolResult> CreateWorktreeAsync(string workspace, string sessionId, string? branch = null,
        string startPoint = "HEAD", CancellationToken ct = default)
    {
        ValidateWorktreeSession(sessionId);
        var repository = await WorktreeRepositoryAsync(workspace, ct);
        var gate = WorktreeLocks.GetOrAdd(repository.CommonDirectory, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if ((await ReadWorktreesAsync(repository, ct)).Length >= 100)
                throw new InvalidOperationException("Managed worktree limit reached. Inspect or remove an owned worktree first.");
            var id = Guid.NewGuid().ToString("N");
            branch ??= "codex/worktree-" + id[..12];
            if (branch.Length is < 1 or > 200 || branch.StartsWith('-') || branch.Any(char.IsControl))
                throw new ArgumentException("Use a valid new Git branch name.");
            await WorktreeGitAsync(repository.Root, ["check-ref-format", "--branch", branch], ct);
            if (string.IsNullOrWhiteSpace(startPoint) || startPoint.Length > 1024 || startPoint.Any(char.IsControl))
                throw new ArgumentException("Use a bounded Git commit or reference as startPoint.");
            var commit = (await WorktreeGitAsync(repository.Root,
                ["rev-parse", "--verify", "--end-of-options", startPoint + "^{commit}"], ct)).Trim();
            if (commit.Length is not (40 or 64) || !commit.All(Uri.IsHexDigit))
                throw new InvalidDataException("Git did not resolve a commit object.");
            CheckPhysicalPath(repository.Bucket);
            Directory.CreateDirectory(Path.Combine(repository.Bucket, ".metadata"));
            CheckPhysicalPath(repository.Bucket);
            var path = Path.Combine(repository.Bucket, id);
            if (Directory.Exists(path) || File.Exists(path)) throw new IOException("Managed worktree destination already exists.");
            var record = new ManagedWorktree(id, sessionId, repository.CommonDirectory, path, branch, commit, DateTimeOffset.UtcNow);
            if (JsonSerializer.Serialize(record, WorktreeJsonContext.Default.ManagedWorktree).Length > 12000)
                throw new ArgumentException("Managed worktree ownership paths exceed the metadata budget.");
            // Persist the intended ownership before Git mutates anything. Cancellation or an interrupted
            // checkout leaves inspectable metadata; it never triggers a force-delete or a replay.
            await SaveWorktreeAsync(repository.Bucket, record, ct);
            var result = await ExecuteAsync("git", ["--no-pager", "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=/dev/null", "-C", repository.Root,
                "worktree", "add", "-b", branch, "--", path, commit], repository.Root, 120, null, ct);
            var output = result.Output[..SafeTextEnd(result.Output, 0, Math.Min(result.Output.Length, 8000))];
            return JsonResult(new { worktreeId = id, path, branch, startCommit = commit,
                created = result.ExitCode == 0 && !result.TimedOut, output,
                outputTruncated = result.Truncated || result.Output.Length > output.Length,
                outcome = result.ExitCode == 0 && !result.TimedOut ? "created" : "unknown_or_failed",
                hint = result.ExitCode == 0 && !result.TimedOut ? "Select this path explicitly to work in the isolated checkout." :
                    "Inspect list_worktrees and worktree_status before retrying. No branch or path was automatically deleted." },
                isError: result.ExitCode != 0 || result.TimedOut || result.Truncated);
        }
        finally { gate.Release(); }
    }

    public async Task<ToolResult> ListWorktreesAsync(string workspace, CancellationToken ct = default, int offset = 0, int limit = 50)
    {
        if (offset is < 0 or > 100 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(offset));
        var repository = await WorktreeRepositoryAsync(workspace, ct);
        var registered = await RegisteredWorktreesAsync(repository.Root, ct);
        var records = await ReadWorktreesAsync(repository, ct);
        var page = records.OrderBy(record => record.Id, StringComparer.Ordinal).Skip(offset).Take(limit).Select(record => new
        {
            worktreeId = record.Id, path = record.Path, branch = record.Branch, sessionId = record.SessionId,
            createdAt = record.CreatedAt, registered = registered.Contains(record.Path, WorktreePathComparer),
            exists = Directory.Exists(record.Path), startCommit = record.StartCommit
        }).ToArray();
        return BoundedPageResult(page, offset, offset + page.Length < records.Length, (items, nextOffset, responseTruncated) => new
        {
            repository = repository.Root, worktrees = items, offset, limit, nextOffset,
            truncated = nextOffset is not null, responseTruncated,
            notice = "Only Cogitide-managed worktrees for this repository are listed. Inventory may change between pages. No old command process is resumed."
        });
    }

    public async Task<ToolResult> WorktreeStatusAsync(string workspace, string worktreeId, CancellationToken ct = default)
    {
        var repository = await WorktreeRepositoryAsync(workspace, ct);
        var record = await OwnedWorktreeAsync(repository, worktreeId, null, ct);
        var registered = (await RegisteredWorktreesAsync(repository.Root, ct)).Contains(record.Path, WorktreePathComparer);
        if (!registered || !Directory.Exists(record.Path))
            return JsonResult(new { worktreeId, path = record.Path, registered, exists = Directory.Exists(record.Path),
                outcome = "missing_or_incomplete", hint = "Inspect Git worktree state manually; this adapter does not prune or recreate unknown state." }, isError: true);
        CheckPhysicalPath(record.Path);
        var status = await WorktreeGitAsync(record.Path, ["status", "--porcelain=v1", "--untracked-files=all", "--ignored=matching"], ct);
        var head = (await WorktreeGitAsync(record.Path, ["rev-parse", "--verify", "HEAD"], ct)).Trim();
        var statusText = status[..SafeTextEnd(status, 0, Math.Min(status.Length, 8000))];
        return JsonResult(new { worktreeId, path = record.Path, branch = record.Branch, head, clean = status.Length == 0,
            status = statusText, statusTruncated = statusText.Length < status.Length });
    }

    public async Task<ToolResult> RemoveWorktreeAsync(string workspace, string sessionId, string worktreeId, CancellationToken ct = default)
    {
        ValidateWorktreeSession(sessionId);
        var repository = await WorktreeRepositoryAsync(workspace, ct);
        var gate = WorktreeLocks.GetOrAdd(repository.CommonDirectory, _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var record = await OwnedWorktreeAsync(repository, worktreeId, sessionId, ct);
            if (!(await RegisteredWorktreesAsync(repository.Root, ct)).Contains(record.Path, WorktreePathComparer))
                throw new InvalidOperationException("Managed worktree is not registered. Unknown state requires manual inspection; no directory was removed.");
            CheckPhysicalPath(record.Path);
            // Git repeats its own dirty/untracked/submodule checks during remove, closing the
            // ordinary status/remove race without any force flag or custom recursive deletion.
            var status = await WorktreeGitAsync(record.Path, ["status", "--porcelain=v1", "--untracked-files=all", "--ignored=matching"], ct);
            if (status.Length != 0) throw new InvalidOperationException("Worktree contains modified, untracked or ignored files. Commit or move them before removal.");
            await WorktreeGitAsync(repository.Root, ["worktree", "remove", "--", record.Path], ct);
            File.Delete(WorktreeMetadataPath(repository.Bucket, worktreeId));
            return JsonResult(new { worktreeId, path = record.Path, removed = true, branch = record.Branch,
                branchRetained = true });
        }
        finally { gate.Release(); }
    }

    private sealed record WorktreeRepository(string Root, string CommonDirectory, string Bucket);
    private static async Task<WorktreeRepository> WorktreeRepositoryAsync(string workspace, CancellationToken ct)
    {
        ValidateWorktreePathText(workspace);
        var root = Root(workspace);
        CheckPhysicalPath(root);
        // --show-toplevel rejects bare repositories and selections inside .git. Git configuration
        // must not redirect an unrelated selected directory into a different checkout's authority.
        var topLevel = await WorktreeGitDirectoryAsync(root, "--show-toplevel", ct);
        CheckPhysicalPath(topLevel);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!root.Equals(topLevel, comparison) && !root.StartsWith(topLevel + Path.DirectorySeparatorChar, comparison))
            throw new UnauthorizedAccessException("Select a physical directory inside this Git checkout, not Git internals or an unrelated worktree path.");
        var common = await WorktreeGitDirectoryAsync(root, "--git-common-dir", ct);
        CheckPhysicalPath(common);
        var registered = await RegisteredWorktreesAsync(root, ct);
        var primary = registered.FirstOrDefault() ?? throw new InvalidDataException("Git has no primary worktree.");
        CheckPhysicalPath(primary);
        // Git's first porcelain record is its primary worktree, so selecting an existing managed
        // checkout does not create nested management directories or change repository ownership.
        var parent = Directory.GetParent(primary)?.FullName ?? throw new InvalidOperationException("A filesystem-root repository cannot host sibling worktrees.");
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? common.ToUpperInvariant() : common)))[..24];
        var bucket = Path.Combine(parent, ".cogitide-worktrees", key);
        CheckPhysicalPath(bucket);
        return new(root, common, bucket);
    }

    private static async Task<string[]> RegisteredWorktreesAsync(string root, CancellationToken ct)
    {
        var output = await WorktreeGitAsync(root, ["worktree", "list", "--porcelain", "-z"], ct);
        var paths = new List<string>();
        foreach (var field in output.Split('\0'))
            if (field.StartsWith("worktree ", StringComparison.Ordinal))
            {
                var path = field[9..];
                ValidateWorktreePathText(path);
                if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Git returned a nonabsolute worktree directory.");
                paths.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
            }
        return paths.ToArray();
    }

    private static async Task<string> WorktreeGitDirectoryAsync(string root, string option, CancellationToken ct)
    {
        var output = await WorktreeGitAsync(root, ["rev-parse", "--path-format=absolute", option], ct);
        if (!output.EndsWith('\n')) throw new InvalidDataException("Git returned an incomplete directory response.");
        var path = output[..^1];
        // Windows cannot contain CR in a valid directory name. On Unix a literal CR is data,
        // so reject it rather than stripping a possible name suffix as if it were a line ending.
        if (OperatingSystem.IsWindows() && path.EndsWith('\r')) path = path[..^1];
        ValidateWorktreePathText(path);
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new InvalidDataException("Git directory is unavailable.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static void ValidateWorktreePathText(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            throw new ArgumentException("Managed worktree directory names must not contain control characters.");
    }

    private static async Task<string> WorktreeGitAsync(string root, string[] arguments, CancellationToken ct)
    {
        // The shared ExecuteAsync Git branch removes all ambient GIT_* and *_API_KEY values.
        // Reuse that policy rather than maintaining a second redirect-variable allow/deny list.
        var result = await ExecuteAsync("git", ["--no-pager", "--no-optional-locks", "-c", "core.fsmonitor=false", "-C", root, .. arguments], root, 30, null, ct);
        if (result.ExitCode != 0 || result.TimedOut || result.Truncated)
            throw new InvalidOperationException("Git worktree operation failed or its result is incomplete: " + result.Output);
        return result.Output;
    }

    private static async Task<ManagedWorktree[]> ReadWorktreesAsync(WorktreeRepository repository, CancellationToken ct)
    {
        var metadata = Path.Combine(repository.Bucket, ".metadata");
        CheckPhysicalPath(metadata);
        if (!Directory.Exists(metadata)) return [];
        var files = Directory.EnumerateFiles(metadata, "*.json").Take(101).ToArray();
        if (files.Length > 100) throw new InvalidOperationException("Managed worktree inventory exceeds 100; inspect existing worktrees before creating more.");
        var records = new List<ManagedWorktree>();
        foreach (var file in files)
            records.Add(await OwnedWorktreeAsync(repository, Path.GetFileNameWithoutExtension(file), null, ct));
        return records.ToArray();
    }

    private static async Task<ManagedWorktree> OwnedWorktreeAsync(WorktreeRepository repository, string id, string? sessionId, CancellationToken ct)
    {
        var file = WorktreeMetadataPath(repository.Bucket, id);
        CheckPhysicalPath(file);
        if (!File.Exists(file)) throw new KeyNotFoundException("Managed worktree was not found for this repository.");
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length > 16384) throw new InvalidDataException("Invalid worktree ownership metadata.");
        var record = await JsonSerializer.DeserializeAsync(input, WorktreeJsonContext.Default.ManagedWorktree, ct)
            ?? throw new InvalidDataException("Missing worktree ownership metadata.");
        if (record.Id != id || string.IsNullOrWhiteSpace(record.SessionId) ||
            !WorktreePathComparer.Equals(record.Repository, repository.CommonDirectory) ||
            !WorktreePathComparer.Equals(record.Path, Path.Combine(repository.Bucket, id)))
            throw new UnauthorizedAccessException("Worktree ownership metadata does not match this repository and managed path.");
        if (sessionId is not null && record.SessionId != sessionId)
            throw new UnauthorizedAccessException("Only the chat that created this managed worktree may remove it.");
        return record;
    }

    private static string WorktreeMetadataPath(string bucket, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid managed worktree ID.");
        return Path.Combine(bucket, ".metadata", id + ".json");
    }
    private static async Task SaveWorktreeAsync(string bucket, ManagedWorktree record, CancellationToken ct)
    {
        var path = WorktreeMetadataPath(bucket, record.Id);
        CheckPhysicalPath(path);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(record, WorktreeJsonContext.Default.ManagedWorktree), ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void ValidateWorktreeSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256) throw new ArgumentException("Managed worktree mutation requires an owning chat.");
    }
    private static void CheckPhysicalPath(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var part in full[current.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Managed worktrees must use physical paths without symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ManagedWorktree))]
internal partial class WorktreeJsonContext : JsonSerializerContext;
