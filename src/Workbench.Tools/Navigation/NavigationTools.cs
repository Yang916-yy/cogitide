using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileSystemGlobbing;
using Workbench.Protocol;

namespace Workbench.Tools;

public sealed partial class WorkspaceService
{
    private const int NavigationFileBytes = 16 * 1024 * 1024;
    private const int NavigationPageCharacters = 12_000;
    private const int NavigationScanFiles = 2000;
    private const int NavigationScanBytes = 32 * 1024 * 1024;
    private sealed record NavigationText(string Text, string Sha256, int Bytes);
    private readonly record struct NavigationLine(int Start, int ContentEnd, int Next);
    private sealed record NavigationContext(int Line, string Text, bool Truncated);
    private sealed record NavigationMatch(string Path, string Sha256, int Line, int Column, int MatchLength,
        string Text, int TextStartColumn, bool TextTruncated, NavigationContext[] Before, NavigationContext[] After);

    /// <summary>Zero-based line/UTF-16 character cursors. Newlines are preserved; oversized lines remain reachable.</summary>
    public async Task<ToolResult> ReadFilePageAsync(string workspace, string path, int offset = 0, int limit = 200,
        int characterOffset = 0, string? expectedSha256 = null, CancellationToken ct = default)
    {
        ValidateNavigationPage(offset, limit);
        if (characterOffset is < 0 or > NavigationFileBytes) throw new ArgumentOutOfRangeException(nameof(characterOffset));
        var full = Resolve(workspace, path);
        var document = await ReadNavigationTextAsync(full, ct);
        if (expectedSha256 is not null && !document.Sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            return new("File changed between pages. Restart at offset 0 and use the new full-file sha256; no stale page was returned.", true);
        var text = document.Text;
        var lineStart = 0;
        for (var line = 0; line < offset; line++)
        {
            if (lineStart >= text.Length) return new("Line offset is past the end of the file.", true);
            lineStart = NavigationLineAt(text, lineStart).Next;
        }
        var first = NavigationLineAt(text, lineStart);
        if (characterOffset > first.ContentEnd - first.Start) return new("Character offset is past this line's content.", true);
        var start = lineStart + characterOffset;
        if (start < text.Length && start > 0 && char.IsLowSurrogate(text[start]) && char.IsHighSurrogate(text[start - 1]))
            return new("Character offset splits a Unicode scalar. Use the returned nextCharacterOffset.", true);
        var requestedEnd = lineStart;
        for (var line = 0; line < limit && requestedEnd < text.Length; line++)
            requestedEnd = NavigationLineAt(text, requestedEnd).Next;
        var end = SafeTextEnd(text, start, Math.Min(requestedEnd, start + NavigationPageCharacters));
        while (true)
        {
            if (end == start && start < text.Length) return new("The page metadata leaves no room for a complete character. Use a shorter workspace-relative path.", true);
            var nextLine = offset;
            var nextStart = lineStart;
            int? endLine = null;
            while (nextStart < end)
            {
                var current = NavigationLineAt(text, nextStart);
                endLine = nextLine + 1;
                if (current.Next > end) break;
                nextStart = current.Next;
                nextLine++;
            }
            var truncated = end < text.Length;
            var result = JsonResult(new
            {
                path, sha256 = document.Sha256, content = text[start..end], bytes = document.Bytes,
                offset, limit, characterOffset, startLine = offset + 1, startColumn = characterOffset + 1, endLine,
                nextOffset = truncated ? nextLine : (int?)null,
                nextCharacterOffset = truncated ? end - nextStart : (int?)null,
                truncated, pageCharacters = end - start,
                snapshotValidated = expectedSha256 is not null,
                hint = truncated ? "Continue with nextOffset, nextCharacterOffset and expectedSha256=sha256. Offsets are zero-based; line/column labels are one-based." : null
            }, full);
            if (result.Content.Length <= MaxResponseCharacters) return result;
            if (end <= start) return new("The path metadata exceeds the response budget.", true);
            end = SafeTextEnd(text, start, start + (end - start) / 2);
        }
    }

    /// <summary>Matching-line pagination, literal/insensitive by default. Snapshot covers the bounded content scan.</summary>
    public async Task<ToolResult> SearchFilePageAsync(string workspace, string query, string pathPrefix = "", int offset = 0,
        int limit = 100, bool regex = false, string? glob = null, bool caseSensitive = false, int contextLines = 0,
        string? expectedSnapshot = null, CancellationToken ct = default)
    {
        ValidateNavigationPage(offset, limit);
        if (string.IsNullOrWhiteSpace(query) || query.Length > 4096) return new("Search query must contain 1..4096 characters.", true);
        if (contextLines is < 0 or > 10) throw new ArgumentOutOfRangeException(nameof(contextLines));
        Regex? expression = null;
        if (regex)
        {
            try { expression = new Regex(query, RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase), TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { return new("Invalid .NET regular expression. Use literal search or correct the pattern.", true); }
        }
        var matcher = glob is null ? null : NavigationMatcher(glob);
        var prefix = NormalizePrefix(workspace, pathPrefix);
        var inventory = await InventoryAsync(workspace, prefix, ct);
        var files = inventory.Files.Where(path => matcher is null || matcher.Match(path).HasMatches).Order(StringComparer.Ordinal).ToArray();
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendNavigationDigest(digest, new { query, prefix, regex, glob, caseSensitive, contextLines, files, inventory.Source, inventory.Truncated });
        List<NavigationMatch> matches = [];
        var seen = 0;
        var scannedFiles = 0;
        long scannedBytes = 0;
        var skippedFiles = 0;
        var scanTruncated = false;
        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            if (scannedFiles >= NavigationScanFiles) { scanTruncated = true; break; }
            NavigationText document;
            try
            {
                var full = Resolve(workspace, path);
                var length = new FileInfo(full).Length;
                if (length <= NavigationFileBytes && scannedBytes + length > NavigationScanBytes) { scanTruncated = true; break; }
                scannedFiles++;
                document = await ReadNavigationTextAsync(full, ct);
                scannedBytes += document.Bytes;
                if (scannedBytes > NavigationScanBytes) { scanTruncated = true; break; }
                AppendNavigationDigest(digest, new { path, document.Sha256 });
            }
            catch (Exception ex) when (ex is IOException or DecoderFallbackException or UnauthorizedAccessException)
            {
                skippedFiles++;
                AppendNavigationDigest(digest, new { path, skipped = true });
                continue;
            }
            Queue<NavigationContext> before = new();
            var number = 1;
            for (var position = 0; position < document.Text.Length; number++)
            {
                ct.ThrowIfCancellationRequested();
                var line = NavigationLineAt(document.Text, position);
                var body = document.Text.AsSpan(line.Start, line.ContentEnd - line.Start);
                int column, matchLength;
                if (expression is null)
                {
                    column = body.IndexOf(query.AsSpan(), caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                    matchLength = query.Length;
                }
                else
                {
                    try
                    {
                        var match = expression.EnumerateMatches(body);
                        var found = match.MoveNext();
                        column = found ? match.Current.Index : -1;
                        matchLength = found ? match.Current.Length : 0;
                    }
                    catch (RegexMatchTimeoutException)
                    { return new("Regular expression exceeded its 100 ms per-line budget. Narrow the pattern or use literal search; no partial search page was returned.", true); }
                }
                if (column >= 0)
                {
                    if (seen >= offset && matches.Count < limit + 1)
                    {
                        var previewStart = Math.Max(0, column - 160);
                        if (previewStart > 0 && char.IsLowSurrogate(body[previewStart]) && char.IsHighSurrogate(body[previewStart - 1])) previewStart--;
                        var previewEnd = SafeTextEnd(document.Text, line.Start + previewStart, Math.Min(line.ContentEnd, line.Start + previewStart + 500));
                        var after = new List<NavigationContext>();
                        for (var next = line.Next; next < document.Text.Length && after.Count < contextLines;)
                        {
                            var context = NavigationLineAt(document.Text, next);
                            after.Add(NavigationContextAt(document.Text, context, number + after.Count + 1));
                            next = context.Next;
                        }
                        matches.Add(new(path, document.Sha256, number, column + 1, matchLength,
                            document.Text[(line.Start + previewStart)..previewEnd], previewStart + 1,
                            previewStart > 0 || previewEnd < line.ContentEnd, before.ToArray(), after.ToArray()));
                    }
                    seen++;
                }
                if (contextLines > 0)
                {
                    before.Enqueue(NavigationContextAt(document.Text, line, number));
                    if (before.Count > contextLines) before.Dequeue();
                }
                position = line.Next;
            }
        }
        AppendNavigationDigest(digest, new { scannedFiles, scannedBytes, skippedFiles, scanTruncated, seen });
        var snapshot = Convert.ToHexStringLower(digest.GetHashAndReset());
        if (expectedSnapshot is not null && !snapshot.Equals(expectedSnapshot, StringComparison.OrdinalIgnoreCase))
            return new("Search contents or inventory changed between pages. Restart at offset 0; no stale search page was returned.", true);
        var page = matches.Take(limit).ToArray();
        return BoundedPageResult(page, offset, seen > (long)offset + page.Length, (items, nextOffset, responseTruncated) => new
        {
            query, pathPrefix = prefix, regex, glob, caseSensitive, contextLines, matches = items, offset, limit, nextOffset,
            snapshot, snapshotScope = "Content hashes of scanned readable files plus bounded inventory; skipped contents are not verified.",
            totalMatchesInScan = seen, truncated = seen > (long)offset + items.Count || inventory.Truncated || scanTruncated || skippedFiles > 0 || responseTruncated,
            responseTruncated, inventoryTruncated = inventory.Truncated, scanTruncated, skippedFiles, scannedFiles, scannedBytes,
            inventory.Source, inventory.Warning,
            hint = scanTruncated || inventory.Truncated ? "Scan budget reached. Narrow pathPrefix (actual traversal) and/or glob. nextOffset only pages matches within this bounded scan." :
                skippedFiles > 0 ? "Some files were unreadable, changed, binary, non-UTF-8 or larger than 16 MiB; this is not an exhaustive search." : null
        });
    }

    public async Task<ToolResult> GlobFilesAsync(string workspace, string pattern, string pathPrefix = "", int offset = 0,
        int limit = 200, string? expectedSnapshot = null, CancellationToken ct = default)
    {
        ValidateNavigationPage(offset, limit);
        var matcher = NavigationMatcher(pattern);
        var prefix = NormalizePrefix(workspace, pathPrefix);
        var inventory = await InventoryAsync(workspace, prefix, ct);
        var files = inventory.Files.Where(path => matcher.Match(path).HasMatches).Order(StringComparer.Ordinal).ToArray();
        var snapshot = Hash(JsonSerializer.SerializeToUtf8Bytes(new { pattern, prefix, files, inventory.Source, inventory.Truncated }, Wire.Json));
        if (expectedSnapshot is not null && !snapshot.Equals(expectedSnapshot, StringComparison.OrdinalIgnoreCase))
            return new("Matching file inventory changed between pages. Restart at offset 0; no stale glob page was returned.", true);
        var page = files.Skip(offset).Take(limit).ToArray();
        return BoundedPageResult(page, offset, (long)offset + page.Length < files.Length, (items, nextOffset, responseTruncated) => new
        {
            files = items, pattern, pathPrefix = prefix, offset, limit, nextOffset, snapshot, snapshotScope = "Matching file names only; not file contents.",
            totalMatchesInInventory = files.Length, truncated = (long)offset + items.Count < files.Length || inventory.Truncated || responseTruncated,
            responseTruncated, inventoryTruncated = inventory.Truncated, inventory.Source, inventory.SkippedEntries, inventory.Warning,
            hint = inventory.Truncated ? "Inventory budget reached. Narrow pathPrefix before retrying; a glob filters the bounded inventory." : null
        });
    }

    private static Matcher NavigationMatcher(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > 1024 || pattern.Contains('\\') || pattern.Contains(':') || pattern.StartsWith('/') ||
            pattern.Split('/').Any(segment => segment == "..")) throw new ArgumentException("Use a workspace-relative glob with / separators, * and **, without parent traversal.");
        // Microsoft's matcher owns glob parsing. Match against validated Git/fallback paths, never walk again.
        return new Matcher(OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal).AddInclude(pattern);
    }

    private static void ValidateNavigationPage(int offset, int limit)
    {
        if (offset is < 0 or > NavigationFileBytes || limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(offset), "offset must be 0..16777216 and limit must be 1..1000.");
    }

    private static async Task<NavigationText> ReadNavigationTextAsync(string full, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var before = new FileInfo(full);
        var beforeLength = before.Length;
        var beforeWrite = before.LastWriteTimeUtc;
        if (beforeLength > NavigationFileBytes) throw new IOException("File exceeds the 16 MiB navigation limit.");
        await using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var bytes = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var count = await file.ReadAsync(buffer, ct);
            if (count == 0) break;
            if (bytes.Length + count > NavigationFileBytes) throw new IOException("File grew beyond the navigation limit.");
            bytes.Write(buffer, 0, count);
        }
        var after = new FileInfo(full);
        if (after.Length != beforeLength || after.LastWriteTimeUtc != beforeWrite || bytes.Length != beforeLength)
            throw new IOException("File changed while reading. Read it again before continuing.");
        var data = bytes.GetBuffer().AsSpan(0, (int)bytes.Length);
        if (data.Contains((byte)0)) throw new IOException("Binary and UTF-16 files are unsupported by the UTF-8 navigation tools.");
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(data));
        bytes.Position = data.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        using var reader = new StreamReader(bytes, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);
        return new(text, sha256, (int)bytes.Length);
    }

    private static NavigationLine NavigationLineAt(string text, int start)
    {
        var relative = text.AsSpan(start).IndexOfAny('\r', '\n');
        if (relative < 0) return new(start, text.Length, text.Length);
        var end = start + relative;
        var next = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
        return new(start, end, next);
    }

    private static int SafeTextEnd(string text, int start, int end)
    {
        if (end > start && end < text.Length &&
            (char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]) || text[end - 1] == '\r' && text[end] == '\n')) end--;
        return end;
    }

    private static NavigationContext NavigationContextAt(string text, NavigationLine line, int number)
    {
        var end = SafeTextEnd(text, line.Start, Math.Min(line.ContentEnd, line.Start + 500));
        return new(number, text[line.Start..end], end < line.ContentEnd);
    }

    private static void AppendNavigationDigest<T>(IncrementalHash hash, T value) =>
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(value, Wire.Json));
}
