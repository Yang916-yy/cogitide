using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Workbench.Protocol;

namespace Workbench.Tools;

internal sealed record CommandRecord(string Id, string SessionId, string RunId, string WorkspacePath,
    string Command, string Status, DateTimeOffset StartedAt, int? ExitCode = null, bool OutputTruncated = false,
    string? Error = null, DateTimeOffset? FinishedAt = null);
internal sealed record CommandLaunch(string Directory, CommandRecord Record, int TimeoutSeconds);

/// <summary>
/// Owned, noninteractive command sessions. A small instance of the existing Host runs each command and
/// watches its parent's stdin pipe: losing the parent cancels the shell and its children.
/// Session/poll/terminal semantics follow Codex unified exec; .NET Process and streams own execution.
/// </summary>
public sealed class CommandSessionManager : IAsyncDisposable, IRunResourceLifetime
{
    internal const int MaximumOutputBytes = 4 * 1024 * 1024;
    private readonly string _directory;
    private readonly Func<ProcessStartInfo> _worker;
    private readonly ConcurrentDictionary<string, LiveCommand> _live = new();
    private readonly SemaphoreSlim _starts = new(1, 1);
    private bool _disposed;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public CommandSessionManager(string directory, Func<ProcessStartInfo> worker)
    {
        _directory = Path.GetFullPath(directory);
        _worker = worker;
        Directory.CreateDirectory(_directory);
        if ((File.GetAttributes(_directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Command storage must be a physical directory.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public async Task<ToolResult> StartAsync(ToolExecutionContext context, string command, int timeoutSeconds = 600,
        int yieldMilliseconds = 1000, CancellationToken ct = default)
    {
        ValidateOwner(context);
        if (string.IsNullOrWhiteSpace(command) || command.Length > 32768)
            throw new ArgumentException("Command must contain 1 to 32768 characters.");
        if (JsonSerializer.Serialize(command, Wire.Json).Length > 40000)
            throw new ArgumentException("Encoded command exceeds the metadata budget. Put long scripts in a workspace file and execute that file.");
        if (timeoutSeconds is < 1 or > 3600 || yieldMilliseconds is < 0 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        var workspace = WorkspaceService.Root(context.WorkspacePath);
        LiveCommand job;
        await _starts.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_live.Values.Count(x => !x.Completion.IsCompleted) >= 16 ||
                _live.Values.Count(x => !x.Completion.IsCompleted && SamePath(x.Record.WorkspacePath, workspace)) >= 4)
                throw new InvalidOperationException("Command session limit reached. Wait for or stop an owned command.");
            var id = Guid.NewGuid().ToString("N");
            var directory = Path.Combine(_directory, id);
            Directory.CreateDirectory(directory);
            var record = new CommandRecord(id, context.SessionId!, context.RunId!, workspace, command, "starting", DateTimeOffset.UtcNow);
            await SaveRecordAsync(directory, record);
            var start = _worker();
            start.WorkingDirectory = workspace;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            start.StandardInputEncoding = new UTF8Encoding(false);
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
            foreach (var variable in context.ExcludedEnvironmentVariables ?? []) start.Environment.Remove(variable);
            var process = new Process { StartInfo = start };
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!process.Start()) throw new IOException("Command worker could not start.");
                job = new(process, record, directory);
                _live[id] = job;
                job.Completion = MonitorAsync(job);
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new CommandLaunch(directory, record, timeoutSeconds), CommandJsonContext.Default.CommandLaunch).AsMemory(), ct);
                await process.StandardInput.FlushAsync(ct);
                job.Registration = ct.Register(static state => ((LiveCommand)state!).Stop(), job);
            }
            catch
            {
                try { if (!process.HasExited) process.Kill(true); } catch (Exception) { }
                process.Dispose();
                await SaveRecordAsync(directory, record with { Status = "failed", Error = "The command worker did not start.", FinishedAt = DateTimeOffset.UtcNow });
                throw;
            }
        }
        finally { _starts.Release(); }
        // The worker drains its own stdout/stderr; this control pipe contains no command output.
        if (yieldMilliseconds > 0)
            await Task.WhenAny(job.Completion, Task.Delay(yieldMilliseconds, ct));
        ct.ThrowIfCancellationRequested();
        return await ReadAsync(context, job.Record.Id, ct: ct);
    }

    public async Task<ToolResult> ReadAsync(ToolExecutionContext context, string commandId, long offset = 0,
        int maxCharacters = 12000, int waitMilliseconds = 0, CancellationToken ct = default)
    {
        ValidateOwner(context);
        if (offset < 0 || offset > MaximumOutputBytes || maxCharacters is < 2 or > 12000 ||
            waitMilliseconds is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(offset));
        var directory = JobDirectory(commandId);
        var record = await LoadRecordAsync(directory, ct);
        CheckOwner(record, context);
        if (waitMilliseconds > 0 && _live.TryGetValue(commandId, out var job) && !job.Completion.IsCompleted)
            await Task.WhenAny(job.Completion, Task.Delay(waitMilliseconds, ct));
        ct.ThrowIfCancellationRequested();
        record = await LoadRecordAsync(directory, ct);
        CheckOwner(record, context);
        // A vanished worker cannot be resumed by PID or by replaying the command.
        if (record.Status is "starting" or "running" && !_live.ContainsKey(commandId))
        {
            // Completion may have been persisted and the worker removed since the preceding read.
            record = await LoadRecordAsync(directory, ct);
            CheckOwner(record, context);
            if (record.Status is "starting" or "running")
                record = record with { Status = "interrupted", Error = "Worker ownership was lost. The result is unknown; inspect current files before retrying." };
        }
        var output = "";
        long length = 0, next = offset;
        var file = Path.Combine(directory, "output.log");
        if (File.Exists(file))
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            length = stream.Length;
            if (offset > length) throw new ArgumentException("Output offset is beyond retained output.");
            stream.Position = offset;
            var buffer = new byte[(int)Math.Min(length - offset, maxCharacters * 4L)];
            var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, ct);
            if (count > 0 && (buffer[0] & 0xC0) == 0x80)
                throw new ArgumentException("Use the exact nextOffset returned by the previous output page.");
            // File pages use byte cursors. Only complete UTF-8 sequences may be consumed.
            var end = count;
            if (end > 0)
            {
                var lead = end - 1;
                while (lead > 0 && (buffer[lead] & 0xC0) == 0x80) lead--;
                var needed = buffer[lead] < 0x80 ? 1 : buffer[lead] < 0xE0 ? 2 : buffer[lead] < 0xF0 ? 3 : 4;
                if (end - lead < needed) end = lead;
            }
            try { output = Utf8.GetString(buffer, 0, end); }
            catch (DecoderFallbackException) { throw new ArgumentException("Use the exact nextOffset returned by the previous output page."); }
            if (output.Length > maxCharacters)
            {
                var take = maxCharacters;
                if (char.IsHighSurrogate(output[take - 1])) take--;
                output = output[..take];
            }
            next += Utf8.GetByteCount(output);
        }
        else if (offset != 0) throw new ArgumentException("Output is not available at this offset.");
        var running = record.Status is "starting" or "running";
        string SerializePage() => JsonSerializer.Serialize(new
        {
            commandId, command = record.Command, status = record.Status, running, exitCode = record.ExitCode,
            timedOut = record.Status == "timed_out", cancelled = record.Status == "cancelled",
            output, offset, nextOffset = next, outputBytes = length, hasMore = next < length,
            outputTruncated = record.OutputTruncated, outputLimitBytes = MaximumOutputBytes,
            error = record.Error, startedAt = record.StartedAt, finishedAt = record.FinishedAt,
            lifetime = "Owned by this agent run; still-running commands stop when the run stops or ends. Logs remain readable in this chat."
        }, Wire.Json);
        var serialized = SerializePage();
        while (serialized.Length > 80000 && output.Length > 2)
        {
            var take = output.Length / 2;
            if (char.IsHighSurrogate(output[take - 1])) take--;
            output = output[..take];
            next = offset + Utf8.GetByteCount(output);
            serialized = SerializePage();
        }
        if (serialized.Length > 80000) throw new InvalidDataException("Command metadata exceeds the response budget.");
        return new(serialized, record.Status is "failed" or "timed_out" or "cancelled" or "interrupted");
    }

    public async Task<ToolResult> StopAsync(ToolExecutionContext context, string commandId, long offset = 0, CancellationToken ct = default)
    {
        var record = await LoadRecordAsync(JobDirectory(commandId), ct);
        CheckOwner(record, context);
        if (_live.TryGetValue(commandId, out var job)) await StopAndWaitAsync(job);
        return await ReadAsync(context, commandId, offset, ct: ct);
    }

    public async Task<int> EndRunAsync(string sessionId, string runId)
    {
        var jobs = _live.Values.Where(x => x.Record.SessionId == sessionId && x.Record.RunId == runId && !x.Completion.IsCompleted).ToArray();
        var unfinished = 0;
        try
        {
            foreach (var job in jobs)
            {
                try
                {
                    var record = await LoadRecordAsync(job.Directory, CancellationToken.None);
                    if (record.SessionId != job.Record.SessionId || record.RunId != job.Record.RunId ||
                        record.Command != job.Record.Command || !SamePath(record.WorkspacePath, job.Record.WorkspacePath) ||
                        record.Status is "starting" or "running") unfinished++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { unfinished++; }
            }
        }
        finally
        {
            // Metadata failures must never prevent ownership pipes from closing.
            foreach (var job in jobs) job.Stop();
            await Task.WhenAll(jobs.Select(StopAndWaitAsync));
        }
        return unfinished;
    }

    private async Task MonitorAsync(LiveCommand job)
    {
        // Always drain both pipes. They carry only control diagnostics, never retained task output.
        var stdout = job.Process.StandardOutput.ReadToEndAsync();
        var stderr = job.Process.StandardError.ReadToEndAsync();
        try
        {
            await job.Process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
            var diagnostic = (await stderr).Trim();
            if (!diagnostic.StartsWith("Command worker failed: ", StringComparison.Ordinal) || diagnostic.Length > 200) diagnostic = "";
            var record = await LoadRecordAsync(job.Directory, CancellationToken.None);
            if (record.Status is "running" or "starting")
                await SaveRecordAsync(job.Directory, record with { Status = "interrupted",
                    Error = "Command worker exited without a terminal result. Do not replay blindly. " + diagnostic, FinishedAt = DateTimeOffset.UtcNow });
        }
        finally
        {
            job.Registration.Dispose();
            _live.TryRemove(job.Record.Id, out _);
            job.Process.Dispose();
        }
    }

    private static async Task StopAndWaitAsync(LiveCommand job)
    {
        job.Stop();
        try { await job.Completion.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException)
        {
            try { if (!job.Process.HasExited) job.Process.Kill(true); } catch (InvalidOperationException) { }
            await job.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private string JobDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid command session ID.");
        var directory = Path.Combine(_directory, id);
        if (!Directory.Exists(directory)) throw new KeyNotFoundException("Command session not found.");
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("Invalid command storage.");
        return directory;
    }
    private static void ValidateOwner(ToolExecutionContext context)
    {
        if (string.IsNullOrWhiteSpace(context.SessionId) || string.IsNullOrWhiteSpace(context.RunId))
            throw new ArgumentException("Background commands require an owning agent session and run.");
    }
    private static void CheckOwner(CommandRecord record, ToolExecutionContext context)
    {
        ValidateOwner(context);
        if (record.SessionId != context.SessionId || !SamePath(record.WorkspacePath, WorkspaceService.Root(context.WorkspacePath)))
            throw new UnauthorizedAccessException("Command output belongs to another chat or workspace.");
    }
    private static bool SamePath(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static async Task<CommandRecord> LoadRecordAsync(string directory, CancellationToken ct)
    {
        await using var input = await OpenRecordAsync(directory, ct);
        if (input.Length > 128 * 1024) throw new InvalidDataException("Invalid command record size.");
        var bytes = new byte[input.Length];
        await input.ReadExactlyAsync(bytes, ct);
        var record = JsonSerializer.Deserialize(bytes, CommandJsonContext.Default.CommandRecord) ?? throw new InvalidDataException("Invalid command record.");
        if (record.Id != Path.GetFileName(directory) || string.IsNullOrWhiteSpace(record.SessionId) ||
            string.IsNullOrWhiteSpace(record.RunId) || string.IsNullOrWhiteSpace(record.WorkspacePath) ||
            string.IsNullOrWhiteSpace(record.Command) || record.StartedAt == default)
            throw new InvalidDataException("Incomplete command record.");
        var coherent = record.Status switch
        {
            "starting" or "running" => record.ExitCode is null && record.FinishedAt is null,
            "completed" => record.ExitCode == 0 && record.FinishedAt is not null,
            "failed" => record.ExitCode != 0 && record.FinishedAt is not null,
            "cancelled" or "timed_out" or "interrupted" => record.ExitCode is null && record.FinishedAt is not null,
            _ => false
        };
        if (!coherent) throw new InvalidDataException("Inconsistent command status.");
        return record;
    }
    private static async Task<FileStream> OpenRecordAsync(string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, "session.json");
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            }
            catch (IOException ex) when (OperatingSystem.IsWindows() && attempt < 5 &&
                (ex.HResult == unchecked((int)0x80070002) || ex.HResult == unchecked((int)0x80070020)) && Directory.Exists(directory))
            {
                // Windows snapshot replacement can briefly make the name unavailable or deny a new
                // read handle. Reopen only these observed errors; parsing and permission failures propagate.
                await Task.Delay(10 << attempt, ct);
            }
        }
    }
    internal static async Task SaveRecordAsync(string directory, CommandRecord record)
    {
        var temporary = Path.Combine(directory, "session." + Guid.NewGuid().ToString("N") + ".tmp");
        var stage = "write_snapshot";
        try
        {
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(record, CommandJsonContext.Default.CommandRecord));
            stage = "replace_snapshot";
            var destination = Path.Combine(directory, "session.json");
            // Move(overwrite: true) can fail on Windows even when an existing reader permits deletion.
            // Replace preserves the old reader's snapshot without rewriting it in place. Windows can
            // briefly reject new opens during replacement; OpenRecordAsync handles that bounded window.
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
        }
        catch (Exception ex)
        {
            ex.Data["CommandRecordStage"] = stage;
            throw;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex)
            {
                ex.Data["CommandRecordStage"] = "cleanup_snapshot";
                throw;
            }
        }
    }

    /// <summary>Entry point used exclusively by the application's hidden child worker.</summary>
    public static async Task RunWorkerAsync()
    {
        var stage = "read_launch";
        try
        {
            var line = await Console.In.ReadLineAsync();
            stage = "parse_launch";
            if (line is null || line.Length > 128 * 1024) throw new InvalidDataException("Invalid command worker request.");
            var launch = JsonSerializer.Deserialize(line, CommandJsonContext.Default.CommandLaunch) ?? throw new InvalidDataException("Invalid command launch.");
            if (launch.TimeoutSeconds is < 1 or > 3600 || launch.Record.Command.Length is < 1 or > 32768)
                throw new InvalidDataException("Invalid command limits.");
            stage = "validate_workspace";
            WorkspaceService.Root(launch.Record.WorkspacePath);
            using var disconnected = new CancellationTokenSource();
            // Parent EOF is an ownership loss, not permission to continue detached work.
            var watch = Task.Run(async () => { try { await Console.In.ReadLineAsync(); } finally { disconnected.Cancel(); } });
            var record = launch.Record with { Status = "running" };
            stage = "persist_running";
            await SaveRecordAsync(launch.Directory, record);
            long bytes = 0;
            var truncated = false;
            stage = "create_output";
            await using var log = new FileStream(Path.Combine(launch.Directory, "output.log"), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            try
            {
                stage = "execute";
                var result = await WorkspaceService.RunSessionCommandAsync(record.WorkspacePath, record.Command, launch.TimeoutSeconds,
                    async (chunk, _) =>
                    {
                        var encoded = Utf8.GetBytes(chunk);
                        if (encoded.Length > MaximumOutputBytes - bytes)
                        {
                            truncated = true;
                            var available = (int)(MaximumOutputBytes - bytes);
                            while (available > 0 && available < encoded.Length && (encoded[available] & 0xC0) == 0x80) available--;
                            encoded = encoded[..available];
                        }
                        if (encoded.Length == 0) return;
                        await log.WriteAsync(encoded);
                        await log.FlushAsync();
                        bytes += encoded.Length;
                    }, disconnected.Token);
                record = record with { Status = result.TimedOut ? "timed_out" : result.ExitCode == 0 ? "completed" : "failed", ExitCode = result.TimedOut ? null : result.ExitCode };
            }
            catch (OperationCanceledException) when (disconnected.IsCancellationRequested) { record = record with { Status = "cancelled" }; }
            catch (Exception) { record = record with { Status = "failed", Error = "The command could not finish. Inspect output and current files before retrying." }; }
            finally
            {
                stage = "flush_output";
                await log.FlushAsync();
                stage = "persist_terminal";
                await SaveRecordAsync(launch.Directory, record with { OutputTruncated = truncated, FinishedAt = DateTimeOffset.UtcNow });
            }
            // The pending stdin read is a background task; process exit closes it.
            _ = watch;
            stage = "dispose_output";
        }
        catch (Exception ex)
        {
            ex.Data["CommandWorkerStage"] = stage;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _starts.WaitAsync();
        try { if (_disposed) return; _disposed = true; }
        finally { _starts.Release(); }
        foreach (var job in _live.Values) job.Stop();
        await Task.WhenAll(_live.Values.Select(StopAndWaitAsync));
        foreach (var job in _live.Values) { job.Registration.Dispose(); job.Process.Dispose(); }
    }
    private sealed class LiveCommand(Process process, CommandRecord record, string directory)
    {
        internal Process Process { get; } = process;
        internal CommandRecord Record { get; } = record;
        internal string Directory { get; } = directory;
        internal Task Completion { get; set; } = Task.CompletedTask;
        internal CancellationTokenRegistration Registration;
        private int _stopped;
        internal void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            try { Process.StandardInput.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CommandRecord))]
[JsonSerializable(typeof(CommandLaunch))]
internal partial class CommandJsonContext : JsonSerializerContext;
