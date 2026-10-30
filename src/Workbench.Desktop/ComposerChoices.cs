using Workbench.Protocol;

namespace Workbench.Desktop;

internal static class ComposerChoices
{
    internal static string NormalizeBackend(string backend) => backend is "codex" or "chatgpt" ? "chatgpt" : "api";

    internal static bool SameModel(ProviderSettings left, ProviderSettings right) =>
        left.Model == right.Model && left.Endpoint.TrimEnd('/') == right.Endpoint.TrimEnd('/') && ResolveProvider(left) == ResolveProvider(right);

    internal static bool RequiresNewSession(DesktopSettings current, DesktopSettings next) =>
        NormalizeBackend(current.Backend) != NormalizeBackend(next.Backend) ||
        (NormalizeBackend(next.Backend) == "api" ? !SameModel(current.Provider, next.Provider) : current.CodexModel != next.CodexModel);

    internal static bool RequiresFreshSession(SessionInfo session, DesktopSettings settings, string? accountBinding) =>
        session.Backend == "codex" || session.Backend != NormalizeBackend(settings.Backend) ||
        (session.Backend == "chatgpt" && session.AccountBinding is not null && accountBinding is not null && session.AccountBinding != accountBinding);

    internal static ProviderKind ResolveProvider(ProviderSettings provider) => provider.Provider != ProviderKind.Auto ? provider.Provider :
        Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint) ? endpoint.Host.ToLowerInvariant() switch
        {
            "api.deepseek.com" => ProviderKind.DeepSeek,
            "api.anthropic.com" => ProviderKind.Anthropic,
            _ => ProviderKind.OpenAiCompatible
        } : ProviderKind.OpenAiCompatible;

    internal static IReadOnlyList<string> ApiEfforts(ProviderSettings provider) => ResolveProvider(provider) switch
    {
        ProviderKind.DeepSeek => ["none", "low", "high", "max"],
        ProviderKind.Anthropic => ["none", "low", "medium", "high", "xhigh", "max"],
        _ => ["none", "minimal", "low", "medium", "high", "xhigh"]
    };

    internal static IReadOnlyList<string> AccountEfforts(ModelInfo? model) => new[] { "none" }
        .Concat(model?.ReasoningEfforts ?? [])
        .Where(value => value is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max")
        .Distinct(StringComparer.Ordinal).ToArray();

    internal static string AccountEffort(ModelInfo? model, string current) =>
        AccountEfforts(model).Contains(current, StringComparer.Ordinal) ? current : "none";

    internal static string EffortKey(string effort, bool serviceDefault) => effort switch
    {
        "none" => serviceDefault ? "ReasoningProviderDefault" : "ReasoningOff",
        "minimal" => "ReasoningMinimal", "low" => "ReasoningLow", "medium" => "ReasoningMedium",
        "high" => "ReasoningHigh", "xhigh" => "ReasoningExtraHigh", "max" => "ReasoningMaximum",
        _ => "ReasoningProviderDefault"
    };

    internal static string ModeKey(ExecutionMode mode) => mode switch
    {
        ExecutionMode.ReadOnly => "ModeReadOnly",
        ExecutionMode.FullAccess => "ModeFullAccess",
        _ => "ModeWorkspaceWrite"
    };
}
