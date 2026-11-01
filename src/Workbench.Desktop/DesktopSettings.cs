using System.Text.Json;
using System.Text.Json.Serialization;
using Workbench.Desktop.Remote;
using Workbench.Protocol;

namespace Workbench.Desktop;

internal sealed record DesktopSettings
{
    public string? LastWorkspace { get; init; }
    public string? LastSessionId { get; init; }
    public string Backend { get; init; } = "unconfigured";
    public bool OnboardingCompleted { get; init; }
    public ProviderSettings Provider { get; init; } = new(Endpoint: "", Model: "", ApiKeyEnvironmentVariable: "");
    // Retain these serialized names so existing model preferences survive the move to direct OAuth inference.
    public string CodexModel { get; init; } = "";
    public string CodexReasoningEffort { get; init; } = "none";
    public ExecutionMode ExecutionMode { get; init; } = ExecutionMode.WorkspaceWrite;
    public IReadOnlyList<ProviderSettings> RecentProviders { get; init; } = [];
    public string? Language { get; init; }
    public bool ReducedMotion { get; init; }
    public ResearchOptions Research { get; init; } = new();
    public RunOptions Run { get; init; } = new();
    public IReadOnlyList<HostConnectionProfile> ConnectionProfiles { get; init; } = [];
    public string ActiveConnectionId { get; init; } = "local";
    [JsonIgnore] public HostConnectionProfile ActiveConnection =>
        (ConnectionProfiles ?? []).FirstOrDefault(profile => profile.Id == ActiveConnectionId) ?? HostConnectionProfile.Local;

    public DesktopSettings RememberProvider(ProviderSettings provider) => this with
    {
        Backend = Backend == "unconfigured" && !string.IsNullOrWhiteSpace(provider.Endpoint) && !string.IsNullOrWhiteSpace(provider.Model) ? "api" : Backend,
        Provider = provider,
        RecentProviders = new[] { provider }.Concat(RecentProviders ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item.Endpoint) && !string.IsNullOrWhiteSpace(item.Model))
            .DistinctBy(item => (item.Endpoint.TrimEnd('/'), item.Model, ComposerChoices.ResolveProvider(item))).Take(8).ToArray()
    };

    public DesktopSettings RememberConnection(HostConnectionProfile connection)
    {
        connection.Validate();
        return this with
        {
            ActiveConnectionId = connection.Id,
            ConnectionProfiles = connection.IsRemote
                ? new[] { connection }.Concat(ConnectionProfiles ?? []).DistinctBy(profile => profile.Id).Take(32).ToArray()
                : ConnectionProfiles ?? []
        };
    }

    public DesktopSettings NormalizeBackend()
    {
        var profiles = (ConnectionProfiles ?? []).Where(profile =>
        {
            if (profile is null || !profile.IsRemote) return false;
            try { profile.Validate(); return true; }
            catch (ArgumentException) { return false; }
        }).DistinctBy(profile => profile.Id).Take(32).ToArray();
        return this with
        {
            Backend = Backend == "unconfigured" && !string.IsNullOrWhiteSpace(Provider.Endpoint) && !string.IsNullOrWhiteSpace(Provider.Model)
                ? "api" : ComposerChoices.NormalizeBackend(Backend), Research = Research ?? new(), Run = Run ?? new(),
            ConnectionProfiles = profiles,
            ActiveConnectionId = profiles.Any(profile => profile.Id == ActiveConnectionId) ? ActiveConnectionId : "local"
        };
    }

    private static string SettingsPath => Path.Combine(Program.DataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Workbench"), "desktop-settings.json");

    public static DesktopSettings Load()
    {
        try { return File.Exists(SettingsPath) ? FromJson(File.ReadAllText(SettingsPath)) : new(); }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
    }

    internal static DesktopSettings FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var settings = JsonSerializer.Deserialize<DesktopSettings>(json, Wire.Json) ?? new();
        if (!document.RootElement.TryGetProperty("onboardingCompleted", out _))
        {
            var provider = document.RootElement.TryGetProperty("provider", out _) ? settings.Provider : new ProviderSettings();
            // Older Auto OpenAI settings used Chat Completions. Preserve their explicit persisted behavior.
            if (provider.Provider == ProviderKind.Auto && Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Host == "api.openai.com")
                provider = provider with { Provider = ProviderKind.OpenAiCompatible };
            settings = settings with { Provider = provider, Backend = settings.Backend == "unconfigured" ? "api" : settings.Backend, OnboardingCompleted = true };
        }
        return settings.NormalizeBackend();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Wire.Json));
        File.Move(temporary, SettingsPath, overwrite: true);
    }
}
