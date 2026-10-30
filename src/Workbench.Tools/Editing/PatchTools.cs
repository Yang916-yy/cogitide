// Focused C# port of OpenCode's MIT-licensed Codex-style patch module, modified for Cogitide.
// Original MIT notice, fixed source revision and changes: ../Licenses/Patch/PROVENANCE.md.
using System.Text;
using Workbench.Protocol;

namespace Workbench.Tools;

public sealed partial class WorkspaceService
{
    private const int MaxPatchCharacters = 2_000_000;
    private const int MaxPatchFiles = 64;
    private const int MaxPatchBytes = 8 * 1024 * 1024;
    private static StringComparer PatchPathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>All source and destination paths, parsed by the executor's own parser for scoped rule checks.</summary>
    public static IReadOnlyList<string> DescribePatchPaths(string workspace, string patchText) =>
        ParsePatch(workspace, patchText).SelectMany(hunk => hunk.MovePath is { } move ? new[] { hunk.Path, move } : [hunk.Path]).ToArray();

    /// <summary>
    /// OpenCode's Codex-format parser and ordered hunk application adapted to C#; see Licenses/Patch/PROVENANCE.md.
    /// Preflight every file, stage replacements/backups, then commit each path with revalidation and controlled-error rollback.
    /// This is not a filesystem sandbox or a power-loss-atomic multi-file transaction.
    /// </summary>
    public async Task<ToolResult> ApplyPatchAsync(string workspace, string patchText,
        IReadOnlyDictionary<string, string> expectedHashes, CancellationToken ct = default)
    {
        List<SemaphoreSlim> acquired = [];
        List<PatchFile> files = [];
        List<string> directories = [];
        HashSet<string> retained = new(PatchPathComparer);
        try
        {
            ct.ThrowIfCancellationRequested();
            var hunks = ParsePatch(workspace, patchText);
            ArgumentNullException.ThrowIfNull(expectedHashes);
            if (expectedHashes.Count > MaxPatchFiles) throw new InvalidDataException("Too many expected file hashes.");
            Dictionary<string, string> hashes = new(PatchPathComparer);
            foreach (var (path, hash) in expectedHashes)
            {
                var normalized = NormalizePatchPath(workspace, path);
                if (hash is not { Length: 64 } || !hash.All(char.IsAsciiHexDigit) || !hashes.TryAdd(normalized, hash))
                    throw new InvalidDataException("Each existing patch source requires one unique SHA256 hash.");
            }
            var originals = hunks.Where(hunk => hunk.Kind != PatchKind.Add).Select(hunk => hunk.Path).ToHashSet(PatchPathComparer);
            if (hashes.Count != originals.Count || originals.Any(path => !hashes.ContainsKey(path)))
                throw new InvalidDataException("expectedHashes must contain exactly the Update/Delete/Move source paths; read those files first.");
            var paths = hunks.SelectMany(hunk => hunk.MovePath is { } move ? new[] { hunk.Path, move } : [hunk.Path]).ToArray();
            foreach (var path in paths.Select(path => PatchResolve(workspace, path)).Order(PatchPathComparer))
            {
                var gate = FileLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(ct);
                acquired.Add(gate);
            }

            var bytes = 0;
            var comparisons = 0;
            // No target, temporary file or directory is written before every hunk has passed preflight.
            foreach (var hunk in hunks)
            {
                ct.ThrowIfCancellationRequested();
                var full = PatchResolve(workspace, hunk.Path);
                if (Directory.Exists(full)) throw new InvalidDataException("A patch file path names a directory: " + hunk.Path);
                byte[]? original = null;
                UnixFileMode? mode = null;
                if (hunk.Kind == PatchKind.Add)
                {
                    if (File.Exists(full)) throw new InvalidDataException("Add File must not overwrite an existing file: " + hunk.Path);
                }
                else
                {
                    original = await ReadBoundedAsync(full, ct);
                    if (!Hash(original).Equals(hashes[hunk.Path], StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("File changed since it was read; no patch was applied: " + hunk.Path);
                    _ = Decode(original); // Binary and UTF-16 files are deliberately outside this text tool.
                    if (!OperatingSystem.IsWindows()) mode = File.GetUnixFileMode(full);
                }
                byte[]? updated = hunk.Kind switch
                {
                    PatchKind.Delete => null,
                    PatchKind.Add => new UTF8Encoding(false, true).GetBytes(hunk.AddedLines.Count == 0 ? "" : string.Join('\n', hunk.AddedLines) + "\n"),
                    _ => ApplyPatchChunks(original!, hunk.Chunks, ref comparisons, ct)
                };
                if ((updated?.Length ?? 0) > MaxFileBytes || (bytes += (original?.Length ?? 0) + (updated?.Length ?? 0)) > MaxPatchBytes)
                    throw new InvalidDataException("Patch exceeds the 1 MiB per-file or 8 MiB transaction limit.");
                if (hunk.MovePath is { } destination)
                {
                    var target = PatchResolve(workspace, destination);
                    if (File.Exists(target) || Directory.Exists(target)) throw new InvalidDataException("Move destination already exists: " + destination);
                    // Create the destination before removing the source; rollback handles both paths independently.
                    files.Add(new(destination, target, null, updated, mode, "move", hunk.Path));
                    files.Add(new(hunk.Path, full, original, null, mode, "delete"));
                }
                else files.Add(new(hunk.Path, full, original, updated, mode, hunk.Kind.ToString().ToLowerInvariant()));
            }

            var success = JsonResult(new { changed = true, changes = files.Select(file => new
            {
                path = file.Path, operation = file.Operation, previousSha256 = file.Original is null ? null : Hash(file.Original),
                sha256 = file.Updated is null ? null : Hash(file.Updated), sourcePath = file.SourcePath
            }).ToArray() });
            if (success.Content.Length > MaxResponseCharacters)
                throw new InvalidDataException("Patch result metadata exceeds its response budget; split this patch into smaller batches.");

            foreach (var file in files)
            {
                EnsurePatchParents(workspace, file.Path, directories);
                if (file.Original is { } original) file.Backup = await StagePatchFileAsync(workspace, file, original, ".backup", ct);
                if (file.Updated is { } updated) file.Staged = await StagePatchFileAsync(workspace, file, updated, ".new", ct);
            }
            foreach (var file in files) await ValidatePatchOriginalAsync(workspace, file, ct);
            try
            {
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    await ValidatePatchOriginalAsync(workspace, file, ct);
                    if (file.Staged is { } staged)
                    {
                        PatchResolve(workspace, Path.GetRelativePath(Root(workspace), staged));
                        if (!(await ReadBoundedAsync(staged, ct)).AsSpan().SequenceEqual(file.Updated))
                            throw new IOException("Staged patch content changed before commit: " + file.Path);
                        File.Move(staged, file.FullPath, file.Original is not null);
                    }
                    else File.Delete(file.FullPath);
                    file.Applied = true;
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
            {
                var recovery = await RollbackPatchAsync(workspace, files, retained);
                if (recovery.Count > 0)
                    return JsonResult(new { changed = true, rollbackComplete = false, recoveryFiles = recovery,
                        error = "Patch failed and rollback could not safely restore every path. External edits were preserved; inspect the recovery files before continuing." }, isError: true);
                if (error is OperationCanceledException) throw;
                return new ToolResult("Patch failed during commit; all committed file changes were rolled back. " + error.Message, true);
            }
            return success;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { return new ToolResult("Patch was not applied: " + error.Message, true); }
        finally
        {
            foreach (var temporary in files.SelectMany(file => new[] { file.Staged, file.Backup }).OfType<string>())
                if (!retained.Contains(temporary)) TryDeletePatchTemporary(workspace, temporary);
            foreach (var directory in directories.AsEnumerable().Reverse())
                try
                {
                    PatchResolve(workspace, Path.GetRelativePath(Root(workspace), directory));
                    if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { }
            foreach (var gate in acquired.AsEnumerable().Reverse()) gate.Release();
        }
    }

    private static async Task ValidatePatchOriginalAsync(string workspace, PatchFile file, CancellationToken ct)
    {
        PatchResolve(workspace, file.Path);
        if (Directory.Exists(file.FullPath)) throw new IOException("Patch target became a directory: " + file.Path);
        if (file.Original is null)
        {
            if (File.Exists(file.FullPath)) throw new IOException("Patch target was created concurrently: " + file.Path);
        }
        else if (!(await ReadBoundedAsync(file.FullPath, ct)).AsSpan().SequenceEqual(file.Original))
            throw new IOException("Patch source changed while preparing the transaction: " + file.Path);
    }

    private static async Task<IReadOnlyList<string>> RollbackPatchAsync(string workspace, List<PatchFile> files, HashSet<string> retained)
    {
        List<string> recovery = [];
        foreach (var file in files.Where(file => file.Applied).Reverse())
        {
            try
            {
                PatchResolve(workspace, file.Path);
                if (Directory.Exists(file.FullPath)) throw new IOException();
                if (file.Updated is null ? File.Exists(file.FullPath) :
                    !File.Exists(file.FullPath) || !(await ReadBoundedAsync(file.FullPath, CancellationToken.None)).AsSpan().SequenceEqual(file.Updated))
                    throw new IOException(); // Never overwrite a third party's edit during rollback.
                if (file.Backup is { } backup)
                {
                    PatchResolve(workspace, Path.GetRelativePath(Root(workspace), backup));
                    if (!(await ReadBoundedAsync(backup, CancellationToken.None)).AsSpan().SequenceEqual(file.Original)) throw new IOException();
                    File.Move(backup, file.FullPath, file.Updated is not null);
                }
                else File.Delete(file.FullPath);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
            {
                if (file.Backup is { } backup) { retained.Add(backup); recovery.Add(Path.GetRelativePath(Root(workspace), backup).Replace('\\', '/')); }
                else recovery.Add(file.Path);
            }
        }
        return recovery;
    }

    private static void EnsurePatchParents(string workspace, string path, List<string> created)
    {
        var full = PatchResolve(workspace, path);
        var root = Root(workspace);
        Stack<string> pending = new();
        var directory = Path.GetDirectoryName(full)!;
        while (!PatchPathComparer.Equals(directory, root) && !Directory.Exists(directory))
        { pending.Push(directory); directory = Path.GetDirectoryName(directory)!; }
        while (pending.TryPop(out directory))
        {
            PatchResolve(workspace, Path.GetRelativePath(root, directory));
            Directory.CreateDirectory(directory);
            created.Add(directory);
        }
    }

    private static async Task<string> StagePatchFileAsync(string workspace, PatchFile file, byte[] data, string suffix, CancellationToken ct)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(file.FullPath)!, ".cogito-patch-" + Guid.NewGuid().ToString("N") + suffix);
        PatchResolve(workspace, Path.GetRelativePath(Root(workspace), temporary));
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            { await stream.WriteAsync(data, ct); await stream.FlushAsync(ct); }
            if (!OperatingSystem.IsWindows() && file.Mode is { } mode) File.SetUnixFileMode(temporary, mode);
            return temporary;
        }
        catch { TryDeletePatchTemporary(workspace, temporary); throw; }
    }

    private static void TryDeletePatchTemporary(string workspace, string temporary)
    {
        try { PatchResolve(workspace, Path.GetRelativePath(Root(workspace), temporary)); File.Delete(temporary); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static string NormalizePatchPath(string workspace, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || Path.IsPathRooted(path)) throw new InvalidDataException("Patch paths must be bounded relative paths.");
        path = path.Replace('\\', '/');
        var segments = path.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.') ||
            segment.Any(character => char.IsControl(character) || "<>:\"|?*".Contains(character)) || IsPatchDeviceName(segment)))
            throw new InvalidDataException("Patch contains a traversal, ambiguous or unsupported file path.");
        PatchResolve(workspace, path);
        return path;
    }

    private static bool IsPatchDeviceName(string segment)
    {
        var stem = segment.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9';
    }

    private static string PatchResolve(string workspace, string path)
    {
        var full = Resolve(workspace, path);
        var relative = Path.GetRelativePath(Root(workspace), full);
        var current = Root(workspace);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("Patch paths cannot follow symbolic links or junctions.");
            if (index < parts.Length - 1 && (attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("A patch parent path is a file.");
        }
        return full;
    }

    private static IReadOnlyList<PatchHunk> ParsePatch(string workspace, string patchText)
    {
        ArgumentNullException.ThrowIfNull(patchText);
        if (patchText.Length > MaxPatchCharacters || patchText.Contains('\0')) throw new InvalidDataException("Patch exceeds its text limit or contains binary data.");
        var lines = patchText.Replace("\r\n", "\n", StringComparison.Ordinal).Trim().Split('\n');
        if (lines.Length is < 3 or > 100_000 || lines.Any(line => line.Contains('\r')) || lines[0] != "*** Begin Patch" || lines[^1] != "*** End Patch")
            throw new InvalidDataException("Use a complete *** Begin Patch / *** End Patch body, without a shell wrapper.");
        List<PatchHunk> hunks = [];
        HashSet<string> paths = new(PatchPathComparer);
        var index = 1;
        var chunkCount = 0;
        while (index < lines.Length - 1)
        {
            if (hunks.Count >= MaxPatchFiles) throw new InvalidDataException("A patch may affect at most 64 source files.");
            var header = lines[index++];
            var kind = header.StartsWith("*** Add File: ", StringComparison.Ordinal) ? PatchKind.Add :
                header.StartsWith("*** Delete File: ", StringComparison.Ordinal) ? PatchKind.Delete :
                header.StartsWith("*** Update File: ", StringComparison.Ordinal) ? PatchKind.Update :
                throw new InvalidDataException("Unexpected patch header at line " + index);
            var path = NormalizePatchPath(workspace, header[(header.IndexOf(": ", StringComparison.Ordinal) + 2)..]);
            if (!paths.Add(path)) throw new InvalidDataException("Patch paths cannot be repeated or overlap a move destination.");
            string? move = null;
            List<string> added = [];
            List<PatchChunk> chunks = [];
            if (kind == PatchKind.Add)
            {
                while (index < lines.Length - 1 && !lines[index].StartsWith("***", StringComparison.Ordinal))
                {
                    if (!lines[index].StartsWith('+')) throw new InvalidDataException("Every Add File content line must start with '+'.");
                    added.Add(lines[index++][1..]);
                }
            }
            else if (kind == PatchKind.Update)
            {
                if (index < lines.Length - 1 && lines[index].StartsWith("*** Move to: ", StringComparison.Ordinal))
                {
                    move = NormalizePatchPath(workspace, lines[index++][13..]);
                    if (!paths.Add(move)) throw new InvalidDataException("Move destinations must be distinct from every other patch path.");
                }
                while (index < lines.Length - 1 && !lines[index].StartsWith("***", StringComparison.Ordinal))
                {
                    if (++chunkCount > 512) throw new InvalidDataException("Patch contains too many hunks.");
                    string? context = null;
                    if (lines[index] == "@@") index++;
                    else if (lines[index].StartsWith("@@ ", StringComparison.Ordinal)) context = lines[index++][3..];
                    else if (chunks.Count > 0) throw new InvalidDataException("Expected an @@ hunk marker.");
                    List<string> oldLines = [];
                    List<PatchLine> newLines = [];
                    var start = index;
                    while (index < lines.Length - 1 && !lines[index].StartsWith("@@", StringComparison.Ordinal) && !lines[index].StartsWith("***", StringComparison.Ordinal))
                    {
                        var line = lines[index++];
                        if (line.Length == 0 || line[0] == ' ')
                        {
                            var text = line.Length == 0 ? "" : line[1..];
                            newLines.Add(new(text, oldLines.Count)); oldLines.Add(text);
                        }
                        else if (line[0] == '-') oldLines.Add(line[1..]);
                        else if (line[0] == '+') newLines.Add(new(line[1..], null));
                        else throw new InvalidDataException("Update lines must begin with space, '+' or '-'.");
                    }
                    if (index == start) throw new InvalidDataException("Empty or invalid update hunk.");
                    var eof = lines[index] == "*** End of File";
                    if (eof) index++;
                    chunks.Add(new(context, oldLines, newLines, eof));
                    if (eof) break;
                }
                if (chunks.Count == 0 && move is null) throw new InvalidDataException("Update File requires a hunk or a Move to destination.");
            }
            hunks.Add(new(kind, path, move, added, chunks));
        }
        if (hunks.Count == 0) throw new InvalidDataException("Patch contains no file operations.");
        var ordered = paths.Order(PatchPathComparer).ToArray();
        for (var left = 0; left < ordered.Length; left++)
            for (var right = left + 1; right < ordered.Length; right++)
                if (ordered[right].StartsWith(ordered[left] + "/", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("Patch file paths cannot also be parent directories of other targets.");
        return hunks;
    }

    private static byte[] ApplyPatchChunks(byte[] original, IReadOnlyList<PatchChunk> chunks, ref int comparisons, CancellationToken ct)
    {
        if (chunks.Count == 0) return original.ToArray();
        var text = Decode(original);
        var lines = SplitPatchLines(text);
        var preferred = lines.Select(line => line.Ending).FirstOrDefault(ending => ending.Length > 0) ?? "\n";
        List<(int Start, int Count, List<PatchTextLine> Lines)> replacements = [];
        var cursor = 0;
        foreach (var chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            if (chunk.Context is { } context) cursor = SeekPatchSequence(lines, [context], cursor, false, ref comparisons, ct) + 1;
            var position = chunk.OldLines.Count == 0 ? lines.Count : SeekPatchSequence(lines, chunk.OldLines, cursor, chunk.EndOfFile, ref comparisons, ct);
            var ending = lines.Skip(position).Take(chunk.OldLines.Count).Select(line => line.Ending).FirstOrDefault(value => value.Length > 0) ?? preferred;
            var replacement = chunk.NewLines.Select((line, offset) => line.ContextIndex is { } old
                ? lines[position + old]
                : new PatchTextLine(line.Text, offset < chunk.OldLines.Count && lines[position + offset].Ending.Length > 0 ? lines[position + offset].Ending : ending)).ToList();
            replacements.Add((position, chunk.OldLines.Count, replacement));
            cursor = position + chunk.OldLines.Count;
        }
        var result = lines.ToList();
        foreach (var replacement in replacements.AsEnumerable().Reverse())
        { result.RemoveRange(replacement.Start, replacement.Count); result.InsertRange(replacement.Start, replacement.Lines); }
        for (var index = 0; index < result.Count - 1; index++)
            if (result[index].Ending.Length == 0) result[index] = result[index] with { Ending = preferred };
        if (result.Count > 0)
            result[^1] = result[^1] with { Ending = lines.Count > 0 && lines[^1].Ending.Length == 0 ? "" : result[^1].Ending.Length > 0 ? result[^1].Ending : preferred };
        var encoding = new UTF8Encoding(original.AsSpan().StartsWith(new byte[] { 239, 187, 191 }), true);
        var content = string.Concat(result.Select(line => line.Text + line.Ending));
        return encoding.GetPreamble().Concat(encoding.GetBytes(content)).ToArray();
    }

    private static List<PatchTextLine> SplitPatchLines(string text)
    {
        List<PatchTextLine> lines = [];
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] is not ('\r' or '\n')) continue;
            var end = index;
            var ending = text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : text[index].ToString();
            if (ending.Length == 2) index++;
            lines.Add(new(text[start..end], ending)); start = index + 1;
        }
        if (start < text.Length) lines.Add(new(text[start..], ""));
        return lines;
    }

    private static int SeekPatchSequence(IReadOnlyList<PatchTextLine> lines, IReadOnlyList<string> pattern, int start, bool eof, ref int comparisons, CancellationToken ct)
    {
        var match = -1;
        for (var index = eof ? lines.Count - pattern.Count : start; index >= start && index <= lines.Count - pattern.Count; index++)
        {
            var same = true;
            for (var offset = 0; offset < pattern.Count; offset++)
            {
                if (++comparisons > 2_000_000) throw new InvalidDataException("Patch context matching exceeded its budget; use smaller hunks.");
                if ((comparisons & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (lines[index + offset].Text != pattern[offset]) { same = false; break; }
            }
            if (same)
            {
                if (match >= 0) throw new InvalidDataException("Patch context matches more than once; provide more surrounding lines or an @@ context anchor.");
                match = index;
            }
            if (eof) break;
        }
        if (match < 0) throw new InvalidDataException("Patch context was not found exactly; read the current file and regenerate the hunk.");
        return match;
    }

    private enum PatchKind { Add, Delete, Update }
    private sealed record PatchHunk(PatchKind Kind, string Path, string? MovePath, IReadOnlyList<string> AddedLines, IReadOnlyList<PatchChunk> Chunks);
    private sealed record PatchChunk(string? Context, IReadOnlyList<string> OldLines, IReadOnlyList<PatchLine> NewLines, bool EndOfFile);
    private sealed record PatchLine(string Text, int? ContextIndex);
    private sealed record PatchTextLine(string Text, string Ending);
    private sealed class PatchFile(string path, string fullPath, byte[]? original, byte[]? updated, UnixFileMode? mode, string operation, string? sourcePath = null)
    {
        public string Path { get; } = path;
        public string FullPath { get; } = fullPath;
        public byte[]? Original { get; } = original;
        public byte[]? Updated { get; } = updated;
        public UnixFileMode? Mode { get; } = mode;
        public string Operation { get; } = operation;
        public string? SourcePath { get; } = sourcePath;
        public string? Staged { get; set; }
        public string? Backup { get; set; }
        public bool Applied { get; set; }
    }
}
