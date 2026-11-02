using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Workbench.Protocol;

namespace Workbench.Tools;

/// <summary>Explicit, project-local package copies. BCL owns HTTP/ZIP; Core supplies the existing skill parser.</summary>
public sealed partial class SkillManagementService(Func<string, string, SkillManifestInfo> validateManifest, HttpClient? httpClient = null,
    TimeSpan? downloadTimeout = null)
{
    public const int MaxPackageFiles = 256;
    public const int MaxPackageBytes = 16 * 1024 * 1024;
    public const int MaxPackageFileBytes = 2 * 1024 * 1024;
    public const int MaxArchiveBytes = 32 * 1024 * 1024;
    public const string DefaultSource = "anthropic";
    public const string DefaultRevision = "ca45fc496a3436251f191354091d557da31c15b5";
    private const string ProvenanceFile = ".cogitide-skill.json";
    private const string SourceNotices = ".cogitide-source-notices";
    private const string Warning = "Upstream skill metadata and instructions are untrusted references, not vendor-verified capabilities. " +
        "Installation copies files only; scripts and dependencies are never run or installed. Review the included license and compatibility before use.";
    private static readonly HttpClient SharedHttp = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly TimeSpan _downloadTimeout = ValidateDownloadTimeout(downloadTimeout);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WorkspaceGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly Dictionary<string, (byte[] Archive, SkillCatalogEntry[] Entries, bool Truncated)> _catalogs = new(StringComparer.Ordinal);
    private sealed record Source(string Id, string Repository, string Revision, string Root, string Label);
    private static readonly Source[] Sources =
    [
        new(DefaultSource, "anthropics/skills", DefaultRevision, "skills", "Anthropic examples (pinned snapshot)"),
        new("openai-legacy", "openai/skills", "49f948faa9258a0c61caceaf225e179651397431", "skills/.curated", "OpenAI legacy catalog (deprecated upstream)")
    ];

    public async Task<SkillCatalogSearchResult> SearchAsync(string query = "", string sourceId = DefaultSource,
        int offset = 0, int limit = 20, CancellationToken ct = default)
    {
        if (query.Length > 256 || offset is < 0 or > 128 || limit is < 1 or > 100)
            throw new ArgumentException("Use a query of at most 256 characters and a bounded result page.");
        var source = FindSource(sourceId);
        var catalog = await CatalogAsync(source, ct);
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matching = catalog.Entries.Where(item => terms.All(term =>
                item.Name.Contains(term, StringComparison.OrdinalIgnoreCase) || item.Description.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(item => terms.Count(term => item.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(item => item.Name, StringComparer.Ordinal).ToArray();
        var page = matching.Skip(offset).Take(limit).ToArray();
        var moreMatchingEntries = offset + page.Length < matching.Length;
        return new(page, source.Label, source.Revision, moreMatchingEntries,
            (source.Id == "openai-legacy" ? "This optional upstream catalog is deprecated. " : "") +
            (catalog.Truncated ? "Some upstream manifests exceed the current discovery/parser bounds and were omitted; pagination cannot recover omitted entries. " : "") + Warning,
            moreMatchingEntries ? offset + page.Length : null, catalog.Truncated);
    }

    public async Task<SkillInstallResult> InstallAsync(string workspace, string? skillId = null, string? repository = null,
        string? revision = null, string? path = null, CancellationToken ct = default)
    {
        byte[] archive;
        string sourceLabel;
        if (!string.IsNullOrWhiteSpace(skillId))
        {
            if (repository is not null || revision is not null || path is not null)
                throw new ArgumentException("Choose either a catalog skillId or repository/revision/path.");
            var separator = skillId.IndexOf(':');
            if (separator < 1) throw new ArgumentException("Use the exact skillId returned by search_skills.");
            var source = FindSource(skillId[..separator]);
            var catalog = await CatalogAsync(source, ct);
            var entry = catalog.Entries.SingleOrDefault(item => item.Id == skillId)
                ?? throw new ArgumentException("The selected skill is not in this pinned catalog.");
            repository = entry.Repository; revision = entry.Revision; path = entry.Path;
            archive = catalog.Archive; sourceLabel = entry.SourceLabel;
        }
        else
        {
            ValidateRepository(repository, revision);
            ValidateRelative(path ?? "");
            archive = await DownloadArchiveAsync(repository!, revision!, ct);
            sourceLabel = "Explicit public GitHub reference";
        }
        var files = ReadPackageArchive(archive, path!);
        var expectedName = path!.Split('/')[^1];
        var manifest = Manifest(files, expectedName);
        if (manifest.Name != expectedName)
            throw new InvalidDataException("The skill manifest name must match its selected directory name.");
        return await PublishAsync(workspace, manifest, files, sourceLabel + ": https://github.com/" + repository,
            revision, path, ct);
    }

    /// <summary>Only a direct user/UI action may read this endpoint-local directory outside the workspace.</summary>
    public async Task<SkillInstallResult> ImportAsync(string workspace, string sourcePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
            throw new ArgumentException("Choose an absolute skill directory on the connected Host.");
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        CheckPhysicalSource(source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("The selected skill directory does not exist on this Host.");
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((source, 0));
        var visited = 0;
        long total = 0;
        while (pending.TryPop(out var item))
        {
            ct.ThrowIfCancellationRequested();
            if (++visited > 256) throw new InvalidDataException("Skill package has too many directories.");
            CheckPhysicalSource(item.Directory);
            foreach (var child in Directory.EnumerateFileSystemEntries(item.Directory))
            {
                ct.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(source, child).Replace(Path.DirectorySeparatorChar, '/');
                ValidateRelative(relative);
                CheckPhysicalSource(child);
                if (relative.Equals(ProvenanceFile, StringComparison.OrdinalIgnoreCase))
                {
                    if (Directory.Exists(child)) throw new InvalidDataException("The package contains a directory at its reserved receipt path.");
                    // A prior receipt is local reference data, not authority for this new import. Regenerate
                    // provenance from the selected source files rather than trusting or nesting old receipts.
                    continue;
                }
                if (Directory.Exists(child))
                {
                    if (item.Depth >= 8) throw new InvalidDataException("Skill package exceeds directory depth 8.");
                    pending.Push((child, item.Depth + 1));
                    if (pending.Count > 256) throw new InvalidDataException("Skill package has too many directories.");
                    continue;
                }
                if (files.Count >= MaxPackageFiles) throw new InvalidDataException("Skill package exceeds the 256-file limit.");
                await new WorkspaceService().RequireRegularFileAsync(workspace, child, ct);
                CheckPhysicalSource(child);
                await using var input = new FileStream(child, FileMode.Open, FileAccess.Read, FileShare.Read,
                    8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var bytes = await ReadBoundedAsync(input, MaxPackageFileBytes, ct);
                CheckPhysicalSource(child);
                total += bytes.Length;
                if (total > MaxPackageBytes) throw new InvalidDataException("Skill package exceeds 16 MiB.");
                if (!files.TryAdd(relative, bytes)) throw new InvalidDataException("Skill package has ambiguous duplicate paths.");
            }
        }
        var manifest = Manifest(files, Path.GetFileName(source));
        return await PublishAsync(workspace, manifest, files, "Local directory import", null, source, ct);
    }

    private async Task<(byte[] Archive, SkillCatalogEntry[] Entries, bool Truncated)> CatalogAsync(Source source, CancellationToken ct)
    {
        await _catalogGate.WaitAsync(ct);
        try
        {
            if (_catalogs.TryGetValue(source.Id, out var cached)) return cached;
            var bytes = await DownloadArchiveAsync(source.Repository, source.Revision, ct);
            using var stream = new MemoryStream(bytes, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var root = ArchiveRoot(zip);
            var prefix = root + source.Root + "/";
            var entries = zip.Entries.Where(entry => entry.FullName.StartsWith(prefix, StringComparison.Ordinal) &&
                entry.FullName.EndsWith("/SKILL.md", StringComparison.Ordinal) &&
                !entry.FullName[(prefix.Length)..^9].Contains('/')).OrderBy(entry => entry.FullName, StringComparer.Ordinal).Take(129).ToArray();
            var result = new List<SkillCatalogEntry>();
            var truncated = entries.Length > 128;
            foreach (var entry in entries.Take(128))
            {
                ct.ThrowIfCancellationRequested();
                CheckZipEntry(entry);
                if (entry.Length > 64 * 1024) { truncated = true; continue; }
                var relative = entry.FullName[root.Length..^9];
                var folder = relative.Split('/')[^1];
                SkillManifestInfo manifest;
                try { manifest = validateManifest(relative + "/SKILL.md", Decode(ReadEntry(entry, 64 * 1024))); }
                catch (Exception error) when (error is ArgumentException or InvalidDataException)
                { truncated = true; continue; }
                if (manifest.Name != folder) { truncated = true; continue; }
                var license = zip.Entries.FirstOrDefault(item => item.FullName.StartsWith(root + relative + "/", StringComparison.Ordinal) &&
                    !item.FullName[(root.Length + relative.Length + 1)..].Contains('/') && IsNotice(Path.GetFileName(item.FullName)));
                result.Add(new(source.Id + ":" + manifest.Name, manifest.Name, manifest.Description, source.Repository, source.Revision,
                    relative, "https://github.com/" + source.Repository + "/tree/" + source.Revision + "/" + relative,
                    license?.FullName[root.Length..], source.Label));
            }
            var value = (bytes, result.ToArray(), truncated);
            _catalogs.Add(source.Id, value);
            return value;
        }
        finally { _catalogGate.Release(); }
    }

    private async Task<byte[]> DownloadArchiveAsync(string repository, string revision, CancellationToken ct)
    {
        ValidateRepository(repository, revision);
        var url = "https://codeload.github.com/" + repository + "/zip/" + revision;
        using var deadline = new CancellationTokenSource(_downloadTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Cogitide-Skills/1.0");
        try
        {
            // ResponseHeadersRead ends HttpClient's request timeout after headers. This linked deadline
            // stays alive through every bounded body read, including a server that stops sending bytes.
            using var response = await (httpClient ?? SharedHttp).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxArchiveBytes)
                throw new InvalidDataException("The source archive exceeds the 32 MiB download limit.");
            await using var input = await response.Content.ReadAsStreamAsync(linked.Token);
            return await ReadBoundedAsync(input, MaxArchiveBytes, linked.Token);
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new IOException("Skill archive download timed out before completion; no skill was installed.", error);
        }
    }

    private static TimeSpan ValidateDownloadTimeout(TimeSpan? timeout)
    {
        var value = timeout ?? TimeSpan.FromSeconds(90);
        if (value <= TimeSpan.Zero || value > TimeSpan.FromSeconds(90))
            throw new ArgumentOutOfRangeException(nameof(timeout), "A skill download deadline must be positive and no longer than 90 seconds.");
        return value;
    }

    private static Dictionary<string, byte[]> ReadPackageArchive(byte[] bytes, string skillPath)
    {
        ValidateRelative(skillPath);
        using var stream = new MemoryStream(bytes, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var root = ArchiveRoot(zip);
        var prefix = root + skillPath + "/";
        Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var selected = entry.FullName.StartsWith(prefix, StringComparison.Ordinal);
            var rootNotice = entry.FullName.StartsWith(root, StringComparison.Ordinal) &&
                !entry.FullName[root.Length..].Contains('/') && IsNotice(entry.FullName[root.Length..]);
            if (!selected && !rootNotice) continue;
            CheckZipEntry(entry);
            if (entry.FullName.EndsWith('/')) continue;
            var relative = selected ? entry.FullName[prefix.Length..] : SourceNotices + "/" + entry.FullName[root.Length..];
            ValidateRelative(relative);
            if (relative.Split('/').Length > 9) throw new InvalidDataException("Skill package exceeds directory depth 8.");
            if (files.Count >= MaxPackageFiles || entry.Length > MaxPackageFileBytes || (total += entry.Length) > MaxPackageBytes)
                throw new InvalidDataException("Skill package exceeds its file count or byte limits.");
            if (!files.TryAdd(relative, ReadEntry(entry, MaxPackageFileBytes)))
                throw new InvalidDataException("Skill archive has duplicate or ambiguous paths.");
        }
        if (!files.Keys.Any(path => Path.GetFileName(path).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The selected upstream skill has no license file; import a reviewed local package instead.");
        return files;
    }

    private SkillManifestInfo Manifest(Dictionary<string, byte[]> files, string fallbackName)
    {
        if (!files.Keys.Contains("SKILL.md", StringComparer.Ordinal) || !files.TryGetValue("SKILL.md", out var content) || content.Length > 64 * 1024)
            throw new InvalidDataException("Choose a skill directory containing a bounded SKILL.md.");
        if (files.ContainsKey(ProvenanceFile) || files.Keys.Any(path => path.StartsWith(SourceNotices + "/", StringComparison.OrdinalIgnoreCase) &&
            !IsNotice(Path.GetFileName(path))))
            throw new InvalidDataException("The package contains reserved Cogitide provenance paths.");
        return validateManifest(fallbackName + "/SKILL.md", Decode(content));
    }

    private static async Task<SkillInstallResult> PublishAsync(string workspace, SkillManifestInfo manifest,
        Dictionary<string, byte[]> files, string source, string? revision, string? sourcePath, CancellationToken ct)
    {
        var root = WorkspaceService.Root(workspace);
        ValidateRelative(manifest.Name);
        if (manifest.Name.Contains('/')) throw new InvalidDataException("A skill name must be one directory segment.");
        var gate = WorkspaceGates.GetOrAdd(root, _ => new(1, 1));
        await gate.WaitAsync(ct);
        string? staging = null;
        try
        {
            var destinationRelative = ".agents/skills/" + manifest.Name;
            var destination = WorkspaceService.Resolve(root, destinationRelative);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new IOException("A skill directory with this name already exists; it was not overwritten.");
            Directory.CreateDirectory(WorkspaceService.Resolve(root, ".agents/skills"));
            WorkspaceService.Resolve(root, ".agents/skills");
            staging = ".agents/skills/.install-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(WorkspaceService.Resolve(root, staging));
            foreach (var (relative, content) in files)
            {
                ct.ThrowIfCancellationRequested();
                var target = WorkspaceService.Resolve(root, staging + "/" + relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                WorkspaceService.Resolve(root, staging + "/" + relative);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous);
                await output.WriteAsync(content, ct);
            }
            var hash = Convert.ToHexStringLower(SHA256.HashData(files["SKILL.md"]));
            var provenance = JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1, source, revision, sourcePath, installedAt = DateTimeOffset.UtcNow,
                manifestSha256 = hash, scriptsExecuted = false,
                files = files.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
                { path = pair.Key, bytes = pair.Value.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(pair.Value)) })
            }, Wire.Json);
            await File.WriteAllBytesAsync(WorkspaceService.Resolve(root, staging + "/" + ProvenanceFile), provenance, ct);
            ct.ThrowIfCancellationRequested();
            // Same-volume directory rename publishes only a complete validated package and never replaces an existing skill.
            Directory.Move(WorkspaceService.Resolve(root, staging), WorkspaceService.Resolve(root, destinationRelative));
            staging = null;
            return new(manifest.Name, destinationRelative, hash, files.Count, files.Values.Sum(value => (long)value.Length),
                source, revision, sourcePath, destinationRelative + "/" + ProvenanceFile);
        }
        finally
        {
            if (staging is not null) CleanupStaging(root, staging);
            gate.Release();
        }
    }

    private static void CleanupStaging(string root, string staging)
    {
        // Never recursively delete a path that workspace content could have replaced with a link.
        try
        {
            var pending = new Stack<string>(); pending.Push(staging);
            var directories = new List<string>();
            while (pending.TryPop(out var relative))
            {
                var path = WorkspaceService.Resolve(root, relative);
                if (!Directory.Exists(path)) continue;
                directories.Add(relative);
                foreach (var entry in Directory.EnumerateFileSystemEntries(path))
                {
                    var child = Path.GetRelativePath(root, entry);
                    var checkedPath = WorkspaceService.Resolve(root, child);
                    if (Directory.Exists(checkedPath)) pending.Push(child);
                    else File.Delete(checkedPath);
                }
            }
            foreach (var relative in directories.AsEnumerable().Reverse()) Directory.Delete(WorkspaceService.Resolve(root, relative));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Preserve untrusted leftovers; never widen cleanup. */ }
    }

    private static Source FindSource(string sourceId) => Sources.SingleOrDefault(source => source.Id == sourceId)
        ?? throw new ArgumentException("Unknown catalog source. Supported sourceId values: anthropic, openai-legacy.");
    private static void ValidateRepository(string? repository, string? revision)
    {
        if (repository is null || !RepositoryName().IsMatch(repository) || repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ||
            revision is null || !FullRevision().IsMatch(revision))
            throw new ArgumentException("Use a public GitHub owner/repository and an exact 40-character hexadecimal commit, not a branch, URL or credential.");
    }
    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 512 || path.Contains('\\') || Path.IsPathRooted(path))
            throw new InvalidDataException("Skill package requires a bounded relative POSIX path.");
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.EndsWith('.') || segment.EndsWith(' ') || segment.IndexOfAny([':', '<', '>', '"', '|', '?', '*']) >= 0 ||
                segment.Any(char.IsControl) || ReservedDevice().IsMatch(segment))
                throw new InvalidDataException("Skill package contains an unsafe or nonportable path.");
        }
    }
    private static void CheckPhysicalSource(string path)
    {
        var cursor = Path.GetFullPath(path);
        while (true)
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Skill import does not follow symbolic links or junctions.");
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(cursor));
            if (parent is null || parent == cursor) break;
            cursor = parent;
        }
    }
    private static string ArchiveRoot(ZipArchive zip)
    {
        if (zip.Entries.Count is 0 or > 20_000) throw new InvalidDataException("Skill source archive entry count is outside the supported bounds.");
        string? root = null;
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if ((total += entry.Length) > 128L * 1024 * 1024) throw new InvalidDataException("Skill source archive expands beyond 128 MiB.");
            var separator = entry.FullName.IndexOf('/');
            if (separator <= 0) throw new InvalidDataException("Expected a single GitHub source archive root.");
            root ??= entry.FullName[..(separator + 1)];
            if (!entry.FullName.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Source archive has mixed roots.");
            ValidateRelative(entry.FullName.TrimEnd('/'));
        }
        return root!;
    }
    private static void CheckZipEntry(ZipArchiveEntry entry)
    {
        var kind = (entry.ExternalAttributes >> 16) & 0xF000;
        if (kind is not (0 or 0x8000 or 0x4000)) throw new InvalidDataException("Skill archives cannot contain links or special files.");
    }
    private static bool IsNotice(string name) => name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("NOTICE", StringComparison.OrdinalIgnoreCase) || name.Equals("THIRD_PARTY_NOTICES.md", StringComparison.OrdinalIgnoreCase);
    private static byte[] ReadEntry(ZipArchiveEntry entry, int maximum)
    {
        if (entry.Length > maximum) throw new InvalidDataException("Skill package file exceeds its byte limit.");
        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Skill package file exceeds its byte limit.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static async Task<byte[]> ReadBoundedAsync(Stream input, int maximum, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Skill download or file exceeds its byte limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        return output.ToArray();
    }
    private static string Decode(byte[] content)
    {
        var offset = content.Length >= 3 && content.AsSpan(0, 3).SequenceEqual(new byte[] { 239, 187, 191 }) ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(content, offset, content.Length - offset);
    }
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9-]{0,99}/[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryName();
    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex FullRevision();
    [GeneratedRegex("^(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\..*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedDevice();
}
