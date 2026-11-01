namespace Workbench.Protocol;

public sealed record ProviderPreset(string Id, string Name, string Endpoint, ProviderKind Provider, string ApiKeyEnvironmentVariable);
public sealed record ProviderCredentialStatus(bool Configured, string? CredentialProfileId = null);
public sealed record ProviderModelCatalog(IReadOnlyList<ModelInfo> Models, bool Truncated, string Source);

// Classes deliberately retain Object.ToString(): accidental diagnostic formatting must never print API keys.
public sealed class ProviderCredentialSaveRequest
{
    public required ProviderSettings Provider { get; init; }
    public required string ApiKey { get; init; }
    public override string ToString() => nameof(ProviderCredentialSaveRequest);
}
public sealed class ProviderModelsRequest
{
    public required ProviderSettings Provider { get; init; }
    public string? ApiKey { get; init; }
    public override string ToString() => nameof(ProviderModelsRequest);
}

public static class ProviderCredentialBinding
{
    public static string Endpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Provider endpoint must be HTTPS, or loopback HTTP, without embedded credentials, query or fragment.");
        return uri.AbsoluteUri.TrimEnd('/');
    }
    public static ProviderKind Family(ProviderSettings provider)
    {
        if (!Enum.IsDefined(provider.Provider)) throw new ArgumentException("Unknown provider protocol.");
        if (provider.Provider != ProviderKind.Auto) return provider.Provider;
        return new Uri(Endpoint(provider.Endpoint)).Host.ToLowerInvariant() switch
        {
            "api.deepseek.com" => ProviderKind.DeepSeek,
            "api.anthropic.com" => ProviderKind.Anthropic,
            "api.openai.com" => ProviderKind.OpenAiResponses,
            _ => ProviderKind.OpenAiCompatible
        };
    }
}
