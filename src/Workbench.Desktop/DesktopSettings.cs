using System.Text.Json;
using Workbench.Protocol;

namespace Workbench.Desktop;

internal sealed record DesktopSettings
{
    public string? LastWorkspace { get; init; }
    public string? LastSessionId { get; init; }
    public string Backend { get; init; } = "api";
    public ProviderSettings Provider { get; init; } = new();
    // Retain these serialized names so existing model preferences survive the move to direct OAuth inference.
    public string CodexModel { get; init; } = "";
    public string CodexReasoningEffort { get; init; } = "none";
    public ExecutionMode ExecutionMode { get; init; } = ExecutionMode.WorkspaceWrite;
    public IReadOnlyList<ProviderSettings> RecentProviders { get; init; } = [];
    public string? Language { get; init; }
    public bool ReducedMotion { get; init; }
    public ResearchOptions Research { get; init; } = new();

    public DesktopSettings RememberProvider(ProviderSettings provider) => this with
    {
        Provider = provider,
        RecentProviders = new[] { provider }.Concat(RecentProviders ?? [])
            .DistinctBy(item => (item.Endpoint.TrimEnd('/'), item.Model, ComposerChoices.ResolveProvider(item))).Take(8).ToArray()
    };

    public DesktopSettings NormalizeBackend() => this with { Backend = ComposerChoices.NormalizeBackend(Backend), Research = Research ?? new() };

    private static string SettingsPath => Path.Combine(Program.DataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Workbench"), "desktop-settings.json");

    public static DesktopSettings Load()
    {
        try { return File.Exists(SettingsPath) ? (JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath), Wire.Json) ?? new()).NormalizeBackend() : new(); }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Wire.Json));
        File.Move(temporary, SettingsPath, overwrite: true);
    }
}
