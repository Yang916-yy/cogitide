using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Workbench.Protocol;

namespace Workbench.Tools;

public sealed partial class WorkspaceService
{
    private const int MaxInventoryFiles = 20_000;
    private const int MaxInventoryCharacters = 2_000_000;
    private const int MaxResponseCharacters = 80_000;
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public sealed record RepositoryInventory(IReadOnlyList<string> Files, string Source, bool Truncated, int SkippedEntries, string? Warning = null);

    public async Task<ToolResult> ListFilePageAsync(string workspace, string pathPrefix = "", int offset = 0, int limit = 200, CancellationToken ct = default)
    {
        ValidatePage(offset, limit);
        var prefix = NormalizePrefix(workspace, pathPrefix);
        var inventory = await InventoryAsync(workspace, prefix, ct);
        var filtered = inventory.Files.Where(path => path.StartsWith(prefix, PathComparison)).ToArray();
        var files = filtered.Skip(offset).Take(limit).ToArray();
        var more = offset + files.Length < filtered.Length;
        return BoundedPageResult(files, offset, more, (items, nextOffset, responseTruncated) => new { files = items, pathPrefix = prefix, offset, limit, nextOffset,
            truncated = more || inventory.Truncated || responseTruncated, responseTruncated, inventoryTruncated = inventory.Truncated, inventory.Source, inventory.SkippedEntries, inventory.Warning });
    }

    public Task<RepositoryInventory> InventoryAsync(string workspace, CancellationToken ct = default) => InventoryAsync(workspace, "", ct);

    public async Task<RepositoryInventory> InventoryAsync(string workspace, string pathPrefix, CancellationToken ct = default)
    {
        var root = Root(workspace);
        ct.ThrowIfCancellationRequested();
        var prefix = NormalizePrefix(root, pathPrefix);
        var target = Resolve(root, prefix.Length == 0 ? "." : prefix, allowRoot: true);
        // Narrow the actual traversal before its budget, rather than filtering an already-capped whole repository.
        // A trailing slash means a subtree. Without it, retain the old string-prefix semantics (src also matches src2).
        var directory = prefix.Length == 0 || prefix.EndsWith('/') ? target : Path.GetDirectoryName(target)!;
        var relativeDirectory = Path.GetRelativePath(root, directory).Replace(Path.DirectorySeparatorChar, '/');
        string? fallbackWarning;
        try
        {
            var probe = await ReadGitAsync(root, ["rev-parse", "--is-inside-work-tree"], 4096, ct);
            if (probe.ExitCode == 0 && probe.Output.Trim() == "true")
            {
                // Git owns gitignore semantics (including nested negations and global excludes).
                // Without --full-name, paths are relative to the selected cwd, even inside a larger repository.
                var pathspec = relativeDirectory == "." ? "." : ":(literal)" + relativeDirectory + "/";
                var result = await ReadGitAsync(root, ["ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", pathspec], MaxInventoryCharacters, ct);
                if (result.ExitCode != 0 && !result.Truncated) throw new IOException("Git could not enumerate the selected workspace.");
                HashSet<string> files = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                var skipped = 0;
                var truncated = result.Truncated;
                // Never interpret a partial path when an output cap terminates the stream.
                // BCL span splitting avoids allocating an array and strings for rejected prefixes.
                foreach (var range in result.Output.AsSpan().Split('\0'))
                {
                    ct.ThrowIfCancellationRequested();
                    if (range.End.GetOffset(result.Output.Length) == result.Output.Length) break;
                    var entry = result.Output.AsSpan(range);
                    if (!entry.StartsWith(prefix.AsSpan(), PathComparison)) continue;
                    var path = entry.ToString();
                    try
                    {
                        var full = Resolve(root, path);
                        if (!File.Exists(full)) { skipped++; continue; } // Deleted entries and submodule directories are not files.
                        if (files.Count >= MaxInventoryFiles && !files.Contains(path)) { truncated = true; break; }
                        files.Add(path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { skipped++; }
                }
                return new(files.Order(StringComparer.Ordinal).ToArray(), "git", truncated, skipped,
                    truncated ? "Inventory budget reached. Narrow the selected workspace if relevant files are absent." : null);
            }
            if (probe.ExitCode == 0 && probe.Output.Trim() == "false") throw new IOException("The selected directory is Git metadata or a bare repository, not a source worktree.");
            if (HasGitMarker(root)) throw new IOException("A Git worktree marker exists, but Git could not inspect it. Repair repository access or metadata before retrying; ignored files were not scanned.");
            fallbackWarning = "The selected directory is not a Git worktree. Filesystem exclusions apply; gitignore rules were not evaluated.";
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (HasGitMarker(root)) throw new IOException("Git is unavailable for this worktree. Install Git before listing repository files; ignored files were not scanned.");
            fallbackWarning = "Git is unavailable. Filesystem exclusions apply; gitignore rules were not evaluated.";
        }
        return await Task.Run(() => FilesystemInventory(root, fallbackWarning, ct, prefix, directory), ct);
    }

    private static RepositoryInventory FilesystemInventory(string root, string warning, CancellationToken ct, string prefix, string startDirectory)
    {
        List<string> files = [];
        Stack<string> pending = new();
        var relativeStart = Path.GetRelativePath(root, startDirectory);
        if (!Directory.Exists(startDirectory) || (relativeStart != "." && relativeStart.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(SkippedDirectories.Contains)))
            return new(files, "filesystem", false, 0, warning);
        pending.Push(startDirectory);
        var entriesVisited = 0;
        var skipped = 0;
        var truncated = false;
        while (pending.TryPop(out var directory) && !truncated)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory).Take(MaxInventoryFiles * 2 + 1).Order(StringComparer.Ordinal))
                {
                    ct.ThrowIfCancellationRequested();
                    if (++entriesVisited > MaxInventoryFiles * 2 || files.Count >= MaxInventoryFiles) { truncated = true; break; }
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (!SkippedDirectories.Contains(Path.GetFileName(entry))) pending.Push(entry);
                    }
                    else
                    {
                        var relative = Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
                        if (!relative.StartsWith(prefix, PathComparison)) continue;
                        Resolve(root, relative);
                        files.Add(relative);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { skipped++; }
        }
        files.Sort(StringComparer.Ordinal);
        return new(files, "filesystem", truncated, skipped, warning);
    }

    private static void ValidatePage(int offset, int limit)
    {
        if (offset is < 0 or > 100_000 || limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset must be 0..100000 and limit must be 1..1000.");
    }

    private static bool HasGitMarker(string root)
    {
        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
        {
            try { File.GetAttributes(Path.Combine(directory.FullName, ".git")); return true; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return false;
    }

    // Serialize with the shared native JSON contract, then reduce by whole records. The engine's text cap must
    // never turn a successful navigation result into invalid JSON, including six-character Unicode escapes.
    private static ToolResult BoundedPageResult<T>(IReadOnlyList<T> records, int offset, bool more,
        Func<IReadOnlyList<T>, int?, bool, object> envelope, string? artifact = null)
    {
        var count = records.Count;
        while (true)
        {
            var responseTruncated = count < records.Count;
            var next = count > 0 && (more || responseTruncated) ? offset + count : (int?)null;
            var data = envelope(count == records.Count ? records : records.Take(count).ToArray(), next, responseTruncated);
            var json = JsonSerializer.Serialize(data, Wire.Json);
            if (json.Length <= MaxResponseCharacters) return new ToolResult(json, ArtifactPath: artifact);
            if (count <= 1) return new ToolResult("A navigation record exceeds the response budget. Narrow the path or query and inspect the file directly.", true);
            count /= 2;
        }
    }

    private static string NormalizePrefix(string workspace, string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return "";
        Resolve(workspace, prefix, allowRoot: true);
        var normalized = Path.GetRelativePath(Root(workspace), Path.GetFullPath(Path.Combine(Root(workspace), prefix))).Replace(Path.DirectorySeparatorChar, '/');
        if (normalized == ".") return "";
        return prefix.EndsWith('/') || prefix.EndsWith(Path.DirectorySeparatorChar) ? normalized.TrimEnd('/') + "/" : normalized;
    }

    private static async Task<(int ExitCode, string Output, bool Truncated)> ReadGitAsync(string root, string[] arguments, int maximum, CancellationToken ct)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(name);
        foreach (var argument in new[] { "--no-pager", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "-c", "core.quotepath=false" }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var budget = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, budget.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Git could not start.");
        process.StandardInput.Close();
        var stdout = new StringBuilder();
        var truncated = false;
        async Task DrainAsync(StreamReader reader, bool capture)
        {
            var buffer = new char[4096];
            var received = 0;
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), linked.Token);
                if (count == 0) break;
                var cap = capture ? maximum : 8192;
                var remaining = Math.Max(0, cap - received);
                if (capture && remaining > 0) stdout.Append(buffer, 0, Math.Min(count, remaining));
                received += count;
                if (received > cap) { truncated = true; budget.Cancel(); break; }
            }
        }
        var output = DrainAsync(process.StandardOutput, true);
        var error = DrainAsync(process.StandardError, false);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(linked.Token), output, error);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            await StopProcessAsync(process);
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception drainError) when (drainError is OperationCanceledException or IOException or TimeoutException) { }
            ct.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new TimeoutException("Git inventory exceeded its time budget.");
            if (!budget.IsCancellationRequested) throw;
        }
        return (process.ExitCode, stdout.ToString(), truncated);
    }
}
