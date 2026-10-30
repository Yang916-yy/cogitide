using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Workbench.Protocol;

namespace Workbench.Tools;

public sealed partial class WorkspaceService
{
    public const int MaxFileBytes = 1024 * 1024;
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".tools", ".workbench", "bin", "obj", "node_modules", ".venv", "__pycache__" };
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> FileLocks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static string Root(string workspace)
    {
        if (string.IsNullOrWhiteSpace(workspace)) throw new ArgumentException("Choose a workspace first.");
        var root = Path.GetFullPath(workspace);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("Workspace directory does not exist.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Select the physical workspace directory, rather than a symbolic link.");
        return Path.TrimEndingDirectorySeparator(root);
    }

    public static string Resolve(string workspace, string relativePath, bool allowRoot = false)
    {
        var root = Root(workspace);
        if (Path.IsPathRooted(relativePath)) throw new UnauthorizedAccessException("Use a path relative to the workspace.");
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, comparison) && !(allowRoot && full.Equals(root, comparison)))
            throw new UnauthorizedAccessException("Path leaves the selected workspace.");
        if (allowRoot && full.Equals(root, comparison)) return root;
        var relative = Path.GetRelativePath(root, full);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Equals(".git", comparison)) throw new UnauthorizedAccessException("Direct access to Git internals is not allowed.");
            if (OperatingSystem.IsWindows() && (segment.EndsWith(' ') || segment.EndsWith('.')))
                throw new UnauthorizedAccessException("Ambiguous Windows path segments are not supported.");
            if (OperatingSystem.IsWindows() && segment.Contains(':')) throw new UnauthorizedAccessException("Alternate data streams are not supported.");
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Symbolic links and junctions are not followed by workspace tools.");
        }
        return full;
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string workspace, CancellationToken ct = default) =>
        (await InventoryAsync(workspace, ct)).Files.Take(2000).ToArray();

    public async Task<ToolResult> ReadFileAsync(string workspace, string path, CancellationToken ct = default)
    {
        var full = Resolve(workspace, path);
        var bytes = await ReadBoundedAsync(full, ct);
        var content = Decode(bytes);
        return JsonResult(new { path, sha256 = Hash(bytes), content, bytes = bytes.Length }, full);
    }

    public async Task<ToolResult> WriteFileAsync(string workspace, string path, string content, string? expectedSha256, CancellationToken ct = default)
    {
        var full = Resolve(workspace, path);
        var gate = FileLocks.GetOrAdd(full, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            byte[]? previous = null;
            if (File.Exists(full))
            {
                previous = await ReadBoundedAsync(full, ct);
                if (string.IsNullOrWhiteSpace(expectedSha256) || !Hash(previous).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                    return new ToolResult("File changed or expectedSha256 is missing. Read the current file and retry with its SHA256.", true);
            }
            else if (!string.IsNullOrEmpty(expectedSha256))
                return new ToolResult("The expected file no longer exists. Read the workspace again.", true);
            return await WriteCheckedAsync(workspace, path, full, content, previous, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<ToolResult> ReplaceTextAsync(string workspace, string path, string oldText, string newText, string expectedSha256, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(oldText)) return new ToolResult("oldText must not be empty.", true);
        var full = Resolve(workspace, path);
        var gate = FileLocks.GetOrAdd(full, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var bytes = await ReadBoundedAsync(full, ct);
            if (!Hash(bytes).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                return new ToolResult("File changed since it was read; no changes were made.", true);
            var content = Decode(bytes);
            var index = content.IndexOf(oldText, StringComparison.Ordinal);
            if (index < 0 || content.IndexOf(oldText, index + oldText.Length, StringComparison.Ordinal) >= 0)
                return new ToolResult("oldText must match exactly once. Read the file and provide more context.", true);
            var replacement = content[..index] + newText + content[(index + oldText.Length)..];
            return await WriteCheckedAsync(workspace, path, full, replacement, bytes, ct);
        }
        finally { gate.Release(); }
    }

    private static async Task<ToolResult> WriteCheckedAsync(string workspace, string path, string full, string content, byte[]? previous, CancellationToken ct)
    {
        var encoding = new UTF8Encoding(previous is { Length: >= 3 } && previous[0] == 0xEF && previous[1] == 0xBB && previous[2] == 0xBF, true);
        var data = encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();
        if (data.Length > MaxFileBytes) return new ToolResult("File exceeds the 1 MiB text-tool limit.", true);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        Resolve(workspace, path);
        var temporary = full + ".workbench-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, data, ct);
            if (previous is not null)
            {
                var current = await ReadBoundedAsync(full, ct);
                if (!current.AsSpan().SequenceEqual(previous)) return new ToolResult("File changed while preparing the edit; no changes were made.", true);
                // Atomic replacement uses the temporary file's mode; preserve executable and restricted modes.
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(full));
            }
            else if (File.Exists(full)) return new ToolResult("File was created while preparing the edit; no changes were made.", true);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, full, previous is not null);
            return JsonResult(new { path, sha256 = Hash(data), bytes = data.Length, changed = true }, full);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task<ToolResult> SearchAsync(string workspace, string query, CancellationToken ct = default) =>
        SearchAsync(workspace, query, "", 0, 100, ct);

    public async Task<ToolResult> SearchAsync(string workspace, string query, string pathPrefix, int offset, int limit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new ToolResult("Search query must not be empty.", true);
        ValidatePage(offset, limit);
        var prefix = NormalizePrefix(workspace, pathPrefix);
        var inventory = await InventoryAsync(workspace, ct);
        var hits = new List<object>();
        var seen = 0;
        var skippedFiles = 0;
        var more = false;
        var scannedFiles = 0;
        long scannedBytes = 0;
        var scanTruncated = false;
        foreach (var path in inventory.Files.Where(path => path.StartsWith(prefix, PathComparison)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bytes = await ReadBoundedAsync(Resolve(workspace, path), ct);
                if (++scannedFiles > 2000 || (scannedBytes += bytes.Length) > 32 * 1024 * 1024)
                { scanTruncated = true; break; }
                var lines = Decode(bytes).Split('\n');
                for (var i = 0; i < lines.Length; i++)
                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        if (seen++ < offset) continue;
                        if (hits.Count >= limit) { more = true; break; }
                        hits.Add(new { path, line = i + 1, text = lines[i].TrimEnd('\r')[..Math.Min(lines[i].TrimEnd('\r').Length, 500)] });
                    }
            }
            catch (Exception e) when (e is IOException or DecoderFallbackException or UnauthorizedAccessException) { skippedFiles++; }
            if (more) break;
        }
        return BoundedPageResult(hits, offset, more, (items, nextOffset, responseTruncated) => new { query, pathPrefix = prefix, matches = items, offset, limit, nextOffset,
            truncated = more || inventory.Truncated || scanTruncated || responseTruncated, responseTruncated, inventory.Source, inventory.Warning, skippedFiles, scannedFiles, scannedBytes,
            scanTruncated, inventoryTruncated = inventory.Truncated, hint = scanTruncated ? "Search scan budget reached. Narrow pathPrefix and search again." : null });
    }

    public async Task<ToolResult> GitDiffAsync(string workspace, CancellationToken ct = default)
    {
        var root = Root(workspace);
        var status = await ExecuteAsync("git", ["--no-pager", "-c", "core.fsmonitor=false", "status", "--short"], root, 20, null, ct);
        if (status.ExitCode != 0) return new ToolResult(status.Output, true);
        var unstaged = await ExecuteAsync("git", ["--no-pager", "-c", "core.fsmonitor=false", "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--"], root, 20, null, ct);
        var staged = await ExecuteAsync("git", ["--no-pager", "-c", "core.fsmonitor=false", "diff", "--cached", "--no-ext-diff", "--no-textconv", "--no-color", "--"], root, 20, null, ct);
        return new ToolResult($"STATUS (?? denotes untracked files; open those files to review)\n{status.Output}\nUNSTAGED\n{unstaged.Output}\nSTAGED\n{staged.Output}", unstaged.ExitCode != 0 || staged.ExitCode != 0);
    }

    public async Task<ToolResult> RunCommandAsync(string workspace, string command, int timeoutSeconds = 120, Func<string, CancellationToken, ValueTask>? onOutput = null, CancellationToken ct = default,
        IReadOnlyList<string>? excludedEnvironmentVariables = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return new ToolResult("Command must not be empty.", true);
        var result = await RunSessionCommandAsync(workspace, command, Math.Clamp(timeoutSeconds, 1, 600), onOutput, ct, excludedEnvironmentVariables);
        return JsonResult(new { command, exitCode = result.ExitCode, output = result.Output, timedOut = result.TimedOut, truncated = result.Truncated }, isError: result.ExitCode != 0 || result.TimedOut);
    }

    // Host-side setup/evaluation shares the same bounded process, output and cancellation implementation.
    // This argv primitive is deliberately not registered as an agent tool.
    public async Task<ToolResult> RunProgramAsync(string workspace, string executable, string[] arguments,
        int timeoutSeconds = 120, CancellationToken ct = default, IReadOnlyList<string>? excludedEnvironmentVariables = null)
    {
        var result = await ExecuteAsync(executable, arguments, Root(workspace), Math.Clamp(timeoutSeconds, 1, 600), null, ct, excludedEnvironmentVariables);
        return JsonResult(new { exitCode = result.ExitCode, output = result.Output, timedOut = result.TimedOut, truncated = result.Truncated },
            isError: result.ExitCode != 0 || result.TimedOut);
    }

    internal static Task<ProcessResult> RunSessionCommandAsync(string workspace, string command, int timeoutSeconds,
        Func<string, CancellationToken, ValueTask>? onOutput, CancellationToken ct, IReadOnlyList<string>? excludedEnvironmentVariables = null)
    {
        var shell = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh";
        // Windows PowerShell 5 otherwise writes redirected output in its legacy OEM encoding.
        var shellCommand = OperatingSystem.IsWindows()
            ? "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; " + command
            : command;
        string[] args = OperatingSystem.IsWindows() ? ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", shellCommand] : ["-c", shellCommand];
        return ExecuteAsync(shell, args, Root(workspace), timeoutSeconds, onOutput, ct, excludedEnvironmentVariables);
    }

    private static async Task<ProcessResult> ExecuteAsync(string fileName, string[] arguments, string cwd, int timeout, Func<string, CancellationToken, ValueTask>? onOutput, CancellationToken ct,
        IReadOnlyList<string>? excludedEnvironmentVariables = null)
    {
        var start = new ProcessStartInfo(fileName) { WorkingDirectory = cwd, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in new[] { "DEEPSEEK_API_KEY", "OPENAI_API_KEY", "ANTHROPIC_API_KEY" }) start.Environment.Remove(name);
        foreach (var name in excludedEnvironmentVariables ?? []) start.Environment.Remove(name);
        if (string.Equals(Path.GetFileNameWithoutExtension(fileName), "git", StringComparison.OrdinalIgnoreCase))
            foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith("_API_KEY", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(name);
        using var process = new Process { StartInfo = start };
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var output = new StringBuilder();
        var outputGate = new SemaphoreSlim(1, 1);
        var truncated = false;
        Exception? outputError = null;
        linked.Token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("Process did not start.");
        // Commands must not inherit the Host's JSON-RPC stdin pipe. Interactive input belongs to the PTY UI.
        process.StandardInput.Close();
        async Task DrainCoreAsync(StreamReader reader)
        {
            var buffer = new char[2048];
            var carry = "";
            while (true)
            {
                int count;
                try { count = await reader.ReadAsync(buffer.AsMemory(), linked.Token); }
                catch (OperationCanceledException) { return; }
                if (count == 0 && carry.Length == 0) return;
                var chunk = carry + new string(buffer, 0, count);
                carry = "";
                if (count > 0 && char.IsHighSurrogate(chunk[^1])) { carry = chunk[^1].ToString(); chunk = chunk[..^1]; }
                if (count == 0) chunk = "\uFFFD";
                if (chunk.Length == 0) continue;
                await outputGate.WaitAsync(CancellationToken.None);
                try
                {
                    var remaining = 64000 - output.Length;
                    var retained = Math.Min(Math.Max(0, remaining), chunk.Length);
                    if (retained > 0 && retained < chunk.Length && char.IsHighSurrogate(chunk[retained - 1])) retained--;
                    if (retained > 0) output.Append(chunk.AsSpan(0, retained));
                    if (chunk.Length > retained) truncated = true;
                    if (onOutput is not null && !linked.IsCancellationRequested) await onOutput(chunk, linked.Token);
                }
                finally { outputGate.Release(); }
                if (count == 0) return;
            }
        }
        async Task DrainAsync(StreamReader reader)
        {
            try { await DrainCoreAsync(reader); }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref outputError, ex, null);
                linked.Cancel();
                throw;
            }
        }
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(linked.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // This catch is the sole owner of termination. Token callbacks must not race another tree kill.
            await StopProcessAsync(process);
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) when (outputError is not null || e is OperationCanceledException or TimeoutException or IOException) { }
            ct.ThrowIfCancellationRequested();
        }
        catch
        {
            await StopProcessAsync(process);
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception) { }
            throw;
        }
        if (outputError is not null) throw new IOException("Command output could not be retained; the owned process was stopped.", outputError);
        return new ProcessResult(process.HasExited ? process.ExitCode : -1, output.ToString(), timeoutCts.IsCancellationRequested, truncated);
    }

    internal static async Task StopProcessAsync(Process process)
    {
        if (OperatingSystem.IsMacOS())
        {
            await StopMacProcessTreeAsync(process);
            return;
        }
        Exception? failure = null;
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (IsProcessExitRace(ex))
        {
            // Windows tree enumeration can encounter descendants that already exited and report access denied.
            // Still terminate the owned root if the tree operation did not reach it; never silently assume exit.
            failure = ex;
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception fallback) when (IsProcessExitRace(fallback)) { failure = new AggregateException(ex, fallback); }
        }
        try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException ex)
        {
            throw new IOException($"Could not stop command process {process.Id}. It may still be running; inspect it before continuing.", failure ?? ex);
        }
        if (!process.HasExited)
            throw new IOException($"Command process {process.Id} has not exited after termination.", failure);
    }

    // The pinned .NET 11 RC's macOS Kill(true) SIGSTOP path can deadlock its child reaper
    // and signal unrelated members of the caller's process group (dotnet/runtime#131944).
    // Upstream fix: dotnet/runtime#135409, c8b56e6c12e3010e74e1724487a65cbb1c75d0d1.
    // Until that runtime is deployed, use the reported ps-snapshot/BCL Kill() workaround:
    // no native interop, SIGSTOP, process-group signalling, or unbounded background kill.
    // This is best-effort ownership of snapshot descendants, not containment of new forks
    // or deliberately detached processes. Retained start times reject recycled PIDs.
    private static async Task StopMacProcessTreeAsync(Process process)
    {
        if (process.HasExited) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        List<(Process Process, DateTime Started)> descendants = [];
        List<Exception> failures = [];
        try
        {
            var snapshotStarted = DateTime.UtcNow;
            var parents = await ReadMacProcessParentsAsync(deadline.Token);
            var children = parents.GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.Select(pair => pair.Key).ToArray());
            HashSet<int> seen = [process.Id];
            Stack<int> pending = new();
            pending.Push(process.Id);
            while (pending.TryPop(out var parent))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (!children.TryGetValue(parent, out var childIds)) continue;
                foreach (var pid in childIds)
                {
                    if (!seen.Add(pid)) continue;
                    if (seen.Count > 4096) throw new IOException("Owned process cleanup exceeds the 4096-process snapshot budget.");
                    Process? child = null;
                    try
                    {
                        child = Process.GetProcessById(pid);
                        var started = child.StartTime.ToUniversalTime();
                        // A PID born after snapshot collection began cannot be trusted as the
                        // same process seen by ps. Do not signal a potentially recycled PID.
                        if (started > snapshotStarted)
                        {
                            failures.Add(new IOException("A descendant PID changed or was created during process inventory; its cleanup is unverified."));
                            child.Dispose();
                            continue;
                        }
                        descendants.Add((child, started));
                        pending.Push(pid);
                    }
                    catch (ArgumentException) { child?.Dispose(); }
                    catch (Exception ex) when (IsProcessExitRace(ex))
                    {
                        // Missing start-time metadata is not proof that a live child exited.
                        if (child is null || !child.HasExited) failures.Add(ex);
                        child?.Dispose();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { failures.Add(ex); }

        try
        {
            // The traversal records ancestors before descendants. Kill leaves first so ordinary
            // waiting shells can reap children before their own exit, without stopping any process.
            foreach (var owned in descendants.AsEnumerable().Reverse())
            {
                try
                {
                    if (owned.Process.HasExited) continue;
                    using var current = Process.GetProcessById(owned.Process.Id);
                    if (current.StartTime.ToUniversalTime() != owned.Started) continue;
                    owned.Process.Kill();
                }
                catch (ArgumentException) { /* PID is no longer present. */ }
                catch (Exception ex) when (IsProcessExitRace(ex))
                {
                    if (!owned.Process.HasExited) failures.Add(ex);
                }
            }
            try { if (!process.HasExited) process.Kill(); }
            catch (Exception ex) when (IsProcessExitRace(ex))
            { if (!process.HasExited) failures.Add(ex); }
            try
            {
                await Task.WhenAll(descendants.Select(owned => owned.Process.WaitForExitAsync(deadline.Token))
                    .Append(process.WaitForExitAsync(deadline.Token)));
            }
            catch (OperationCanceledException ex) { failures.Add(ex); }
            if (failures.Count > 0)
                throw new IOException("Owned process cleanup could not be fully verified on macOS. Inspect the command before continuing.", new AggregateException(failures));
        }
        finally { foreach (var owned in descendants) owned.Process.Dispose(); }
    }

    private static async Task<Dictionary<int, int>> ReadMacProcessParentsAsync(CancellationToken ct)
    {
        // BSD ps owns process enumeration. Parse only its two numeric fields, never command lines.
        var start = new ProcessStartInfo("/bin/ps")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-A", "-o", "pid=,ppid=" }) start.ArgumentList.Add(argument);
        start.Environment.Clear();
        start.Environment["LC_ALL"] = "C";
        using var snapshot = new Process { StartInfo = start };
        ct.ThrowIfCancellationRequested();
        if (!snapshot.Start()) throw new IOException("Process inventory did not start.");
        snapshot.StandardInput.Close();
        var errors = snapshot.StandardError.ReadToEndAsync(ct);
        try
        {
            Dictionary<int, int> parents = [];
            var characters = 0;
            while (await snapshot.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                characters += line.Length;
                if (characters > 1_048_576 || parents.Count >= 32_768 || line.Length > 64)
                    throw new IOException("Process inventory exceeded its bounded output budget.");
                var values = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (values.Length != 2 || !int.TryParse(values[0], out var pid) || !int.TryParse(values[1], out var parent) ||
                    pid < 0 || parent < 0 || !parents.TryAdd(pid, parent))
                    throw new IOException("Process inventory contained invalid or duplicate process identifiers.");
            }
            await snapshot.WaitForExitAsync(ct);
            await errors;
            if (snapshot.ExitCode != 0) throw new IOException("Process inventory did not complete successfully.");
            return parents;
        }
        catch
        {
            // ps has no command children. A single-process kill avoids the affected runtime path.
            if (!snapshot.HasExited) snapshot.Kill();
            await snapshot.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            try { await errors; } catch (OperationCanceledException) { }
            throw;
        }
    }

    private static bool IsProcessExitRace(Exception exception) => exception switch
    {
        InvalidOperationException => true,
        System.ComponentModel.Win32Exception => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count > 0 && aggregate.InnerExceptions.All(IsProcessExitRace),
        _ => false
    };

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxFileBytes) throw new IOException("File exceeds the 1 MiB text-tool limit.");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (output.Length + count > MaxFileBytes) throw new IOException("File grew beyond the 1 MiB text-tool limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static string Decode(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) throw new IOException("Binary or UTF-16 file is unsupported by the UTF-8 text tool.");
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static ToolResult JsonResult<T>(T data, string? artifact = null, bool isError = false) => new(JsonSerializer.Serialize(data, Wire.Json), isError, artifact);
    internal sealed record ProcessResult(int ExitCode, string Output, bool TimedOut, bool Truncated);
}
