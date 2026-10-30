using System.Security.Cryptography;
using System.Text.Json;
using Workbench.Protocol;

namespace Workbench.Core;

/// <summary>
/// Single-branch speculative actions: heuristic prediction, exact request matching, fresh-content guard.
/// Only the built-in read_file contract is eligible. No model calls, writes, commands or shared cache.
/// </summary>
internal sealed class ReadOnlyPrefetch(string workspace, Func<string, string, bool, string> resolve, int maximum) : IAsyncDisposable
{
    internal sealed record Counters(int Started, int Hits, int Mismatches, int Stale, int Failed);
    private sealed record Request(string FullPath, int Offset, int Limit, int CharacterOffset, string? ExpectedSha256);
    private sealed record Pending(Request Request, Task<ToolResult> Task, CancellationTokenSource Cancellation);
    private Pending? _pending;
    private int _started, _hits, _mismatches, _stale, _failed;
    internal Counters Statistics => new(_started, _hits, _mismatches, _stale, _failed);

    internal static ToolCall? Predict(ToolCall completed, ToolResult result)
    {
        if (result.IsError || completed.Name is not ("search_files" or "glob_files")) return null;
        try
        {
            using var data = JsonDocument.Parse(result.Content);
            string? path = null;
            if (completed.Name == "search_files" && data.RootElement.TryGetProperty("matches", out var matches) &&
                matches.ValueKind == JsonValueKind.Array && matches.GetArrayLength() > 0 &&
                matches[0].TryGetProperty("path", out var matchPath) && matchPath.ValueKind == JsonValueKind.String)
                path = matchPath.GetString();
            if (completed.Name == "glob_files" && data.RootElement.TryGetProperty("files", out var files) &&
                files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0 && files[0].ValueKind == JsonValueKind.String)
                path = files[0].GetString();
            return string.IsNullOrWhiteSpace(path) ? null : new("prefetch-" + Guid.NewGuid().ToString("N"), "read_file",
                JsonSerializer.Serialize(new { path }, Wire.Json));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }

    internal async Task<bool> ScheduleAsync(ToolCall prediction, IAgentTool tool, ToolExecutionContext context, CancellationToken ct)
    {
        await InvalidateAsync();
        if (_started >= maximum || tool.Definition.Name != "read_file" || tool.Definition.RequiresApproval ||
            prediction.Name != "read_file" || Parse(prediction) is not { } request) return false;
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(5));
        _started++;
        // The prediction can overlap the next model request. Its result is never emitted as evidence by itself.
        var execution = Task.Run(() => tool.ExecuteAsync(prediction, context with { OnOutput = null }, linked.Token), linked.Token);
        _pending = new(request, execution, linked);
        return true;
    }

    internal async Task<ToolResult?> TryTakeAsync(ToolCall actual, CancellationToken ct)
    {
        var pending = _pending;
        if (pending is null) return null;
        _pending = null;
        try
        {
            var request = Parse(actual);
            if (request is null || !SamePath(request.FullPath, pending.Request.FullPath) || request.Offset != pending.Request.Offset ||
                request.Limit != pending.Request.Limit || request.CharacterOffset != pending.Request.CharacterOffset)
            {
                _mismatches++;
                return null;
            }
            var result = await pending.Task.WaitAsync(ct);
            if (result.IsError || result.Content.Length > 80000) { _failed++; return null; }
            using var document = JsonDocument.Parse(result.Content);
            if (!document.RootElement.TryGetProperty("sha256", out var hash) || hash.ValueKind != JsonValueKind.String ||
                hash.GetString() is not { Length: 64 } expected ||
                (request.ExpectedSha256 is not null && request.ExpectedSha256 != expected)) { _stale++; return null; }
            // Re-resolve to recheck physical-path policy after the speculative read, then stream a full hash.
            var verified = resolve(workspace, Path.GetRelativePath(workspace, request.FullPath), false);
            await using var input = new FileStream(verified, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (input.Length > 16 * 1024 * 1024) { _stale++; return null; }
            var current = await EvidenceFileHash.ComputeAsync(input, cancellationToken: ct);
            if (current != expected) { _stale++; return null; }
            _hits++;
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or
            InvalidOperationException or OperationCanceledException) { _failed++; return null; }
        finally
        {
            pending.Cancellation.Cancel();
            try { await pending.Task; } catch (Exception) { }
            pending.Cancellation.Dispose();
        }
    }

    internal async Task InvalidateAsync()
    {
        var pending = _pending;
        _pending = null;
        if (pending is null) return;
        pending.Cancellation.Cancel();
        try { await pending.Task; } catch (Exception) { }
        pending.Cancellation.Dispose();
    }

    private Request? Parse(ToolCall call)
    {
        if (call.Name != "read_file") return null;
        try
        {
            using var document = JsonDocument.Parse(call.ArgumentsJson);
            var args = document.RootElement;
            if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(p => p.Name is not
                ("path" or "offset" or "limit" or "characterOffset" or "expectedSha256"))) return null;
            var path = args.GetProperty("path").GetString();
            if (string.IsNullOrWhiteSpace(path)) return null;
            var offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
            var limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 200;
            var character = args.TryGetProperty("characterOffset", out var c) ? c.GetInt32() : 0;
            var hash = args.TryGetProperty("expectedSha256", out var h) && h.ValueKind != JsonValueKind.Null ? h.GetString() : null;
            if (offset < 0 || character < 0 || limit is < 1 or > 1000) return null;
            return new(resolve(workspace, path, false), offset, limit, character, hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException) { return null; }
    }
    private static bool SamePath(string a, string b) => string.Equals(a, b,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public ValueTask DisposeAsync() => new(InvalidateAsync());
}
