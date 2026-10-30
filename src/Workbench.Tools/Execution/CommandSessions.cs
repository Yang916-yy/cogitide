using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Workbench.Protocol;

namespace Workbench.Tools;

internal sealed record CommandRecord(string Id, string SessionId, string RunId, string WorkspacePath,
    string Command, string Status, DateTimeOffset StartedAt, int? ExitCode = null, bool OutputTruncated = false,
    string? Error = null, DateTimeOffset? FinishedAt = null, string Lifetime = "run", string InputMode = "closed",
    DateTimeOffset? ExpiresAt = null);
internal sealed record CommandLaunch(string Directory, CommandRecord Record, int TimeoutSeconds);
internal sealed record CommandInput(int Sequence, string Input, bool CloseInput);
internal sealed record CommandInputReceipt(int Sequence, bool Accepted, string? Error = null);

/// <summary>
/// Owned command sessions. A small instance of the existing Host runs each command and
/// watches its parent's stdin pipe: losing the parent cancels the shell and its children.
/// Session/poll/terminal semantics follow Codex unified exec; .NET Process and streams own execution.
/// </summary>
public sealed class CommandSessionManager : IAsyncDisposable, IRunResourceLifetime
{
    internal const int MaximumOutputBytes = 4 * 1024 * 1024;
    private const int MaximumInputBytes = 16384;
    private const int MaximumTotalInputBytes = 1024 * 1024;
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
        int yieldMilliseconds = 1000, CancellationToken ct = default, bool interactive = false, bool tty = false, string lifetime = "run")
    {
        ValidateOwner(context);
        if (string.IsNullOrWhiteSpace(command) || command.Length > 32768)
            throw new ArgumentException("Command must contain 1 to 32768 characters.");
        if (JsonSerializer.Serialize(command, Wire.Json).Length > 40000)
            throw new ArgumentException("Encoded command exceeds the metadata budget. Put long scripts in a workspace file and execute that file.");
        if (timeoutSeconds is < 1 or > 604800 || yieldMilliseconds is < 0 or > 10000)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        if (lifetime is not ("run" or "session")) throw new ArgumentException("Command lifetime must be run or session.");
        if (tty && !interactive) throw new ArgumentException("tty requires interactive=true.");
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
            var now = DateTimeOffset.UtcNow;
            var record = new CommandRecord(id, context.SessionId!, context.RunId!, workspace, command, "starting", now,
                Lifetime: lifetime, InputMode: tty ? "pty" : interactive ? "pipe" : "closed", ExpiresAt: now.AddSeconds(timeoutSeconds));
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
                try { if (!process.HasExited) await WorkspaceService.StopProcessAsync(process); } catch (Exception) { }
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
            inputMode = record.InputMode, inputClosed = record.InputMode == "closed" || !running ||
                (_live.TryGetValue(commandId, out var owned) && owned.InputClosed), expiresAt = record.ExpiresAt,
            lifetime = record.Lifetime == "session" ?
                "Explicitly owned by this chat until stopped, expiry, chat cleanup or Host loss. It survives ordinary run completion; running is not successful verification." :
                "Owned by this agent run; still-running commands stop when the run stops or ends. Logs remain readable in this chat."
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

    public async Task<ToolResult> WriteAsync(ToolExecutionContext context, string commandId, string input,
        bool closeInput = false, CancellationToken ct = default)
    {
        var record = await LoadRecordAsync(JobDirectory(commandId), ct);
        CheckOwner(record, context);
        if (record.Lifetime == "run" && record.RunId != context.RunId)
            throw new UnauthorizedAccessException("Input to a run-owned command requires its owning run.");
        if (record.InputMode == "closed") throw new InvalidOperationException("This command was started without interactive input.");
        if (record.InputMode == "pty" && closeInput)
            throw new ArgumentException("PTYs have no portable stdin half-close. Send terminal controls explicitly or stop_command; closeInput is supported only for pipe input.");
        var bytes = Utf8.GetByteCount(input);
        if (bytes > MaximumInputBytes || (bytes == 0 && !closeInput))
            throw new ArgumentException("Input must contain 1 to 16384 UTF-8 bytes, or closeInput must be true.");
        if (!_live.TryGetValue(commandId, out var job) || job.Completion.IsCompleted)
            throw new InvalidOperationException("Command worker is no longer owned or running. Input was not sent; commands are never resumed by PID.");
        await job.InputGate.WaitAsync(ct);
        try
        {
            if (job.InputClosed || job.Completion.IsCompleted || job.IsStopped)
                throw new InvalidOperationException("Command input is closed or the worker has stopped.");
            if (job.InputBytes + bytes > MaximumTotalInputBytes)
                throw new InvalidOperationException("Command input reached its 1 MiB lifetime budget.");
            var sequence = ++job.InputSequence;
            var receipt = new TaskCompletionSource<CommandInputReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
            job.InputReceipt = receipt;
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                await job.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new CommandInput(sequence, input, closeInput),
                    CommandJsonContext.Default.CommandInput).AsMemory(), bounded.Token).WaitAsync(bounded.Token);
                await job.Process.StandardInput.FlushAsync(bounded.Token).WaitAsync(bounded.Token);
                var accepted = await receipt.Task.WaitAsync(bounded.Token);
                if (!accepted.Accepted) throw new InvalidOperationException(accepted.Error ?? "Command input was rejected.");
                job.InputBytes += bytes;
                job.InputClosed = closeInput;
                return new(JsonSerializer.Serialize(new { commandId, accepted = true, inputBytes = bytes,
                    totalInputBytes = job.InputBytes, inputClosed = job.InputClosed,
                    notice = "Input was flushed to the owned process stream. Read command output to observe its effect; do not infer command success." }, Wire.Json));
            }
            catch (OperationCanceledException) when (bounded.IsCancellationRequested)
            {
                // A partial write or lost acknowledgement is unknown, never safely replayable.
                job.Stop();
                ct.ThrowIfCancellationRequested();
                throw new IOException("Command input acknowledgement timed out. The owned worker was stopped; input outcome is unknown. Do not replay input blindly.");
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                job.Stop();
                throw new IOException("Command input transport was lost. Input outcome is unknown; inspect output before retrying and never replay blindly.", ex);
            }
            finally { job.InputReceipt = null; }
        }
        finally { job.InputGate.Release(); }
    }

    public async Task<int> EndRunAsync(string sessionId, string runId)
    {
        var jobs = _live.Values.Where(x => x.Record.SessionId == sessionId && x.Record.RunId == runId &&
            x.Record.Lifetime == "run" && !x.Completion.IsCompleted).ToArray();
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

    public async Task<int> EndSessionAsync(string sessionId)
    {
        var jobs = _live.Values.Where(x => x.Record.SessionId == sessionId && !x.Completion.IsCompleted).ToArray();
        foreach (var job in jobs) job.Stop();
        await Task.WhenAll(jobs.Select(StopAndWaitAsync));
        return jobs.Length;
    }

    private async Task MonitorAsync(LiveCommand job)
    {
        // Always drain both pipes. They carry only control diagnostics, never retained task output.
        var stdout = ReadReceiptsAsync(job);
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
            job.InputReceipt?.TrySetException(new IOException("Command worker exited before acknowledging input. Input outcome is unknown; do not replay blindly."));
            job.Registration.Dispose();
            _live.TryRemove(job.Record.Id, out _);
            job.Process.Dispose();
        }
    }

    private static async Task ReadReceiptsAsync(LiveCommand job)
    {
        try
        {
            string? line;
            while ((line = await ReadControlLineAsync(job.Process.StandardOutput, 4096, CancellationToken.None)) is not null)
            {
                var receipt = JsonSerializer.Deserialize(line, CommandJsonContext.Default.CommandInputReceipt)
                    ?? throw new InvalidDataException("Invalid command worker acknowledgement.");
                if (receipt.Sequence != job.InputSequence) throw new InvalidDataException("Unexpected command input acknowledgement.");
                job.InputReceipt?.TrySetResult(receipt);
            }
        }
        catch { job.Stop(); throw; }
    }

    private static async Task StopAndWaitAsync(LiveCommand job)
    {
        job.Stop();
        try { await job.Completion.WaitAsync(TimeSpan.FromSeconds(15)); }
        catch (TimeoutException)
        {
            try { if (!job.Process.HasExited) await WorkspaceService.StopProcessAsync(job.Process); } catch (InvalidOperationException) { }
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
            string.IsNullOrWhiteSpace(record.Command) || record.StartedAt == default ||
            record.Lifetime is not ("run" or "session") || record.InputMode is not ("closed" or "pipe" or "pty"))
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
            var line = await ReadControlLineAsync(Console.In, 128 * 1024, CancellationToken.None);
            stage = "parse_launch";
            if (line is null || line.Length > 128 * 1024) throw new InvalidDataException("Invalid command worker request.");
            var launch = JsonSerializer.Deserialize(line, CommandJsonContext.Default.CommandLaunch) ?? throw new InvalidDataException("Invalid command launch.");
            if (launch.TimeoutSeconds is < 1 or > 604800 || launch.Record.Command.Length is < 1 or > 32768 ||
                launch.Record.Lifetime is not ("run" or "session") || launch.Record.InputMode is not ("closed" or "pipe" or "pty"))
                throw new InvalidDataException("Invalid command limits.");
            stage = "validate_workspace";
            WorkspaceService.Root(launch.Record.WorkspacePath);
            using var disconnected = new CancellationTokenSource();
            using var expiry = new CancellationTokenSource();
            if (launch.Record.ExpiresAt is { } expiresAt)
            {
                var remaining = expiresAt - DateTimeOffset.UtcNow;
                expiry.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
            }
            using var execution = CancellationTokenSource.CreateLinkedTokenSource(disconnected.Token, expiry.Token);
            var inputReady = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
            var inputOperation = new SemaphoreSlim(1, 1);
            // Parent EOF is an ownership loss, not permission to continue detached work.
            // JSON input messages share the existing ownership pipe; the shell never sees Host stdin.
            // Console.In is a synchronized reader whose async methods may block synchronously.
            // Keep the ownership read off the execution path, as the original EOF watcher did.
            var watch = Task.Run(() => WatchInputAsync(launch.Record.InputMode, inputReady.Task, inputOperation, disconnected));
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
                async ValueTask RetainOutputAsync(string chunk, CancellationToken _)
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
                    }
                var result = record.InputMode == "closed"
                    ? await WorkspaceService.RunSessionCommandAsync(record.WorkspacePath, record.Command, launch.TimeoutSeconds,
                        RetainOutputAsync, execution.Token)
                    : await WorkspaceService.RunInputCommandAsync(record.WorkspacePath, record.Command, launch.TimeoutSeconds,
                        record.InputMode == "pty", stream => inputReady.TrySetResult(stream), RetainOutputAsync, execution.Token);
                record = record with { Status = result.TimedOut ? "timed_out" : result.ExitCode == 0 ? "completed" : "failed", ExitCode = result.TimedOut ? null : result.ExitCode };
            }
            catch (OperationCanceledException) when (disconnected.IsCancellationRequested) { record = record with { Status = "cancelled" }; }
            catch (OperationCanceledException) when (expiry.IsCancellationRequested) { record = record with { Status = "timed_out" }; }
            catch (Exception) { record = record with { Status = "failed", Error = "The command could not finish. Inspect output and current files before retrying." }; }
            finally
            {
                inputReady.TrySetCanceled();
                // Closing pipe stdin may let the child exit before its write acknowledgement is
                // flushed. Finish that one in-flight control operation before this worker exits.
                await inputOperation.WaitAsync();
                inputOperation.Release();
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

    private static async Task WatchInputAsync(string inputMode, Task<Stream> inputReady, SemaphoreSlim inputOperation,
        CancellationTokenSource disconnected)
    {
        var sequence = 0;
        var inputBytes = 0;
        var closed = inputMode == "closed";
        try
        {
            string? line;
            while ((line = await ReadControlLineAsync(Console.In, 128 * 1024, disconnected.Token)) is not null)
            {
                await inputOperation.WaitAsync(disconnected.Token);
                try
                {
                var request = JsonSerializer.Deserialize(line, CommandJsonContext.Default.CommandInput)
                    ?? throw new InvalidDataException("Invalid command input request.");
                if (request.Sequence != ++sequence) throw new InvalidDataException("Invalid command input sequence.");
                var bytes = Utf8.GetByteCount(request.Input);
                var error = closed ? "Command input is closed." :
                    inputMode == "pty" && request.CloseInput ? "PTY input has no portable half-close." :
                    bytes > MaximumInputBytes || (bytes == 0 && !request.CloseInput) || inputBytes + bytes > MaximumTotalInputBytes ? "Command input budget exceeded." : null;
                if (error is null)
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(disconnected.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(4));
                    var stream = await inputReady.WaitAsync(deadline.Token);
                    if (bytes > 0) await stream.WriteAsync(Utf8.GetBytes(request.Input), deadline.Token).AsTask().WaitAsync(deadline.Token);
                    await stream.FlushAsync(deadline.Token).WaitAsync(deadline.Token);
                    if (request.CloseInput) { stream.Dispose(); closed = true; }
                    inputBytes += bytes;
                }
                await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new CommandInputReceipt(request.Sequence, error is null, error),
                    CommandJsonContext.Default.CommandInputReceipt));
                await Console.Out.FlushAsync();
                }
                finally { inputOperation.Release(); }
            }
        }
        catch (Exception) { /* Failure invalidates worker ownership; never retry a partial input. */ }
        finally { await disconnected.CancelAsync(); }
    }

    private static async Task<string?> ReadControlLineAsync(TextReader reader, int maximumCharacters, CancellationToken ct)
    {
        var line = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), ct) != 0)
        {
            if (character[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= maximumCharacters) throw new InvalidDataException("Command control message exceeded its budget.");
            line.Append(character[0]);
        }
        if (line.Length != 0) throw new InvalidDataException("Incomplete command control message; ownership was lost.");
        return null;
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
        internal SemaphoreSlim InputGate { get; } = new(1, 1);
        internal TaskCompletionSource<CommandInputReceipt>? InputReceipt;
        internal int InputSequence;
        internal int InputBytes;
        internal bool InputClosed;
        private int _stopped;
        internal bool IsStopped => Volatile.Read(ref _stopped) != 0;
        internal void Stop()
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
            // Dispose the ownership transport without flushing buffered/partial input. A concurrent
            // blocked writer must not delay parent-loss notification or send another partial frame.
            try { Process.StandardInput.BaseStream.Close(); } catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException) { }
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CommandRecord))]
[JsonSerializable(typeof(CommandLaunch))]
[JsonSerializable(typeof(CommandInput))]
[JsonSerializable(typeof(CommandInputReceipt))]
internal partial class CommandJsonContext : JsonSerializerContext;
