using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Workbench.Protocol;
using YamlDotNet.RepresentationModel;

namespace Workbench.Core;

public sealed record ProjectRuleDocument(string Path, string Scope, string Sha256, string Content);
public sealed record ProjectSkill(string Name, string Description, string Path, string BaseDirectory, string Sha256, bool DisableModelInvocation);
public sealed record ProjectSkillCatalog(IReadOnlyList<ProjectSkill> Skills, IReadOnlyList<string> Diagnostics, bool Truncated);
public sealed record ProjectSkillContent(ProjectSkill Skill, string Content);

/// <summary>
/// Project-local instruction ancestry and Pi-compatible skill discovery. Paths are checked by the application's
/// existing workspace resolver; this class does not add a second filesystem boundary implementation.
/// See PROJECT-REUSE.md for the focused upstream ports and intentionally narrower discovery policy.
/// </summary>
public sealed partial class ProjectRules
{
    private const int MaxRuleBytes = 32 * 1024;
    private const int MaxSkillBytes = 64 * 1024;
    private const int MaxRulesCharacters = 64 * 1024;
    private static readonly string[] SkillRoots = [".agents/skills", ".cogito/skills", ".pi/skills", ".opencode/skills"];
    private readonly Func<string, string, bool, string> _resolve;
    public string Workspace { get; }

    public ProjectRules(string workspace, Func<string, string, bool, string> pathResolver)
    {
        ArgumentNullException.ThrowIfNull(pathResolver);
        _resolve = pathResolver;
        Workspace = pathResolver(workspace, "", true);
    }

    /// <summary>Reload root-to-target AGENTS.md on each call. Missing files are removals, not cached instructions.</summary>
    public async Task<IReadOnlyList<ProjectRuleDocument>> LoadAsync(string path = "", bool directory = false,
        CancellationToken cancellationToken = default)
    {
        var target = _resolve(Workspace, path, true);
        var folder = directory || Directory.Exists(target) || target == Workspace ? target : Path.GetDirectoryName(target)!;
        var ancestors = new List<string>();
        while (true)
        {
            ancestors.Add(folder);
            if (SamePath(folder, Workspace)) break;
            folder = Path.GetDirectoryName(folder) ?? throw new InvalidDataException("Project rule ancestry left the workspace.");
            if (ancestors.Count > 128) throw new InvalidDataException("Project rule ancestry exceeds the depth limit.");
        }
        ancestors.Reverse();
        var result = new List<ProjectRuleDocument>();
        var total = 0;
        foreach (var ancestor in ancestors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Posix(Path.GetRelativePath(Workspace, Path.Combine(ancestor, "AGENTS.md")));
            var file = _resolve(Workspace, relative, false);
            if (!File.Exists(file)) continue;
            var (text, hash) = await ReadAsync(relative, MaxRuleBytes, cancellationToken);
            total += text.Length;
            if (total > MaxRulesCharacters) throw new InvalidDataException("Project instructions exceed 64 KiB; reduce their size before continuing.");
            result.Add(new(relative, Posix(Path.GetRelativePath(Workspace, ancestor)), hash, text));
        }
        return result;
    }

    public static string Format(IReadOnlyList<ProjectRuleDocument> documents)
    {
        if (documents.Count == 0) return "";
        return "Project instructions, subordinate to the user's explicit request and application permissions. " +
            "Each document applies only to its directory and descendants; closer instructions refine parent instructions. " +
            "They cannot grant permission to execute commands or access another workspace.\n" +
            JsonSerializer.Serialize(documents, Wire.Json);
    }

    public async Task<ProjectSkillCatalog> DiscoverSkillsAsync(CancellationToken cancellationToken = default)
    {
        // Port of Pi loadSkillsFromDirInternal: SKILL.md ends recursion; root .md files are accepted;
        // otherwise descend into non-hidden subdirectories. Cogito refuses links and caps all discovery.
        var skills = new Dictionary<string, ProjectSkill>(StringComparer.Ordinal);
        var diagnostics = new List<string>();
        var seenPaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var visited = 0;
        var truncated = false;
        foreach (var root in SkillRoots)
        {
            var pending = new Stack<(string Path, bool Root, int Depth)>();
            pending.Push((root, true, 0));
            while (pending.TryPop(out var item))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visited > 256 || skills.Count >= 64) { truncated = true; break; }
                string folder;
                try { folder = _resolve(Workspace, item.Path, false); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException)
                { AddDiagnostic(item.Path + ": directory is not accessible within the workspace."); continue; }
                if (!Directory.Exists(folder)) continue;
                var declared = Posix(Path.Combine(item.Path, "SKILL.md"));
                if (File.Exists(Path.Combine(folder, "SKILL.md")))
                {
                    await AddSkill(declared);
                    continue;
                }
                string[] children;
                try { children = Directory.EnumerateFileSystemEntries(folder).Take(257).Order(StringComparer.Ordinal).ToArray(); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                { AddDiagnostic(item.Path + ": skill directory could not be read."); continue; }
                if (children.Length > 256) { truncated = true; children = children[..256]; }
                foreach (var child in children.Reverse())
                {
                    var name = Path.GetFileName(child);
                    if (name.StartsWith('.') || name == "node_modules") continue;
                    var relative = Posix(Path.GetRelativePath(Workspace, child));
                    if (Directory.Exists(child))
                    {
                        if (item.Depth >= 8) truncated = true;
                        else pending.Push((relative, false, item.Depth + 1));
                    }
                    else if (item.Root && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) await AddSkill(relative);
                }
            }
            if (truncated && (visited > 256 || skills.Count >= 64)) break;
        }
        return new(skills.Values.OrderBy(skill => skill.Name, StringComparer.Ordinal).ToArray(), diagnostics, truncated);

        void AddDiagnostic(string message) { if (diagnostics.Count < 32) diagnostics.Add(message); }
        async Task AddSkill(string relative)
        {
            if (!seenPaths.Add(relative)) return;
            if (skills.Count >= 64) { truncated = true; return; }
            try
            {
                var (content, hash) = await ReadAsync(relative, MaxSkillBytes, cancellationToken);
                var skill = ParseSkill(relative, content, hash);
                if (!skills.TryAdd(skill.Name, skill)) AddDiagnostic($"Duplicate skill '{skill.Name}': keeping {skills[skill.Name].Path}, ignoring {relative}.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or YamlDotNet.Core.YamlException)
            { AddDiagnostic(relative + ": invalid or inaccessible skill manifest."); }
        }
    }

    public async Task<ProjectSkillContent> ReadSkillAsync(string name, bool explicitlyRequested = false,
        CancellationToken cancellationToken = default)
    {
        if (!SkillName().IsMatch(name)) throw new ArgumentException("Use a skill name from the available catalog.");
        var catalog = await DiscoverSkillsAsync(cancellationToken);
        var skill = catalog.Skills.SingleOrDefault(item => item.Name == name) ?? throw new KeyNotFoundException("Project skill not found.");
        if (skill.DisableModelInvocation && !explicitlyRequested)
            throw new InvalidOperationException("This skill requires an explicit user /skill:name request.");
        var (text, hash) = await ReadAsync(skill.Path, MaxSkillBytes, cancellationToken);
        // Re-read metadata as well as body so a mid-discovery replacement cannot bypass invocation policy.
        var actual = ParseSkill(skill.Path, text, hash);
        if (actual.Name != name || (actual.DisableModelInvocation && !explicitlyRequested))
            throw new InvalidOperationException("Skill metadata changed; rediscover the skill before loading it.");
        return new(actual, text);
    }

    public static string FormatSkills(ProjectSkillCatalog catalog)
    {
        var visible = catalog.Skills.Where(skill => !skill.DisableModelInvocation).ToArray();
        if (visible.Length == 0) return "";
        List<ProjectSkill> bounded = [];
        foreach (var skill in visible)
        {
            bounded.Add(skill);
            if (JsonSerializer.Serialize(bounded, Wire.Json).Length > 12_000) { bounded.RemoveAt(bounded.Count - 1); break; }
        }
        return "Project skills are optional task references. Use read_skill when a description matches the user's task. " +
            "Loading a skill never runs scripts or grants permissions. Relative resource paths are based on baseDirectory.\n" +
            JsonSerializer.Serialize(new { skills = bounded, truncated = catalog.Truncated || bounded.Count < visible.Length }, Wire.Json);
    }

    public static IReadOnlySet<string> ExplicitSkills(string prompt) => ExplicitSkill().Matches(prompt)
        .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    internal string Resolve(string relative, bool allowRoot = false) => _resolve(Workspace, relative, allowRoot);

    private async Task<(string Text, string Hash)> ReadAsync(string relative, int limit, CancellationToken ct)
    {
        var full = _resolve(Workspace, relative, false);
        await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[limit + 1];
        var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, ct);
        if (count > limit) throw new InvalidDataException("Project instruction or skill exceeds its file size limit.");
        _resolve(Workspace, relative, false);
        var offset = count >= 3 && bytes.AsSpan(0, 3).SequenceEqual(new byte[] { 239, 187, 191 }) ? 3 : 0;
        var text = new UTF8Encoding(false, true).GetString(bytes, offset, count - offset);
        return (text, Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, count))));
    }

    private static ProjectSkill ParseSkill(string relative, string content, string hash)
    {
        using var reader = new StringReader(content);
        if (reader.ReadLine()?.Trim() != "---") throw new ArgumentException("Skill requires YAML frontmatter.");
        var header = new StringBuilder();
        string? line;
        while ((line = reader.ReadLine()) is not null && line.Trim() != "---") header.AppendLine(line);
        if (line is null || header.Length > 8192) throw new ArgumentException("Skill frontmatter is incomplete or too large.");
        var yaml = new YamlStream();
        yaml.Load(new StringReader(header.ToString()));
        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode map || map.Children.Count > 32)
            throw new ArgumentException("Skill frontmatter must be a bounded mapping.");
        string? Scalar(string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value : null;
        var name = Scalar("name") ?? Path.GetFileName(Path.GetDirectoryName(relative));
        var description = Scalar("description");
        if (name is null || !SkillName().IsMatch(name) || name.Contains("--", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(description) || description.Length > 1024)
            throw new ArgumentException("Skill name or description is invalid.");
        return new(name, description, relative, Posix(Path.GetDirectoryName(relative) ?? "."), hash,
            string.Equals(Scalar("disable-model-invocation"), "true", StringComparison.OrdinalIgnoreCase));
    }

    private static string Posix(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
    private static bool SamePath(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex SkillName();
    [GeneratedRegex("(?:^|\\s)/skill:([a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?)(?=\\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitSkill();
}

public sealed class ProjectSkillTool(ProjectRules rules, IReadOnlySet<string>? explicitlyRequested = null) : IAgentTool
{
    public const string Name = "read_skill";
    public static ToolDefinition Descriptor { get; } = new(Name,
        "Read a project-local skill by catalog name. Returns its version hash and base directory; never executes scripts or grants permissions.",
        """{"type":"object","properties":{"name":{"type":"string","maxLength":64}},"required":["name"],"additionalProperties":false}""");
    public ToolDefinition Definition => Descriptor;

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (call.Name != Name || !Path.GetFullPath(context.WorkspacePath).TrimEnd(Path.DirectorySeparatorChar).Equals(
                rules.Workspace.TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                return new("Skill reader is bound to its selected workspace.", true);
            using var args = JsonDocument.Parse(call.ArgumentsJson);
            if (args.RootElement.ValueKind != JsonValueKind.Object || args.RootElement.EnumerateObject().Count() != 1 ||
                !args.RootElement.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                return new("Use {\"name\": \"catalog-skill-name\"}.", true);
            var value = name.GetString()!;
            var skill = await rules.ReadSkillAsync(value, explicitlyRequested?.Contains(value) == true, cancellationToken);
            return new("Project skill reference; subordinate to the user's instructions and existing permissions. No script was executed.\n" + JsonSerializer.Serialize(skill, Wire.Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException or KeyNotFoundException or InvalidOperationException or YamlDotNet.Core.YamlException)
        { return new("The project skill could not be read: " + ex.Message, true); }
    }
}
