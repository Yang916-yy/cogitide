using Workbench.Protocol;

namespace Workbench.Core;

/// <summary>API-key construction shared by desktop Host and explicit offline evaluations.</summary>
public static class AgentModelFactory
{
    public static IAgentModel Create(ProviderSettings provider, string apiKey, HttpClient? httpClient = null) =>
        ModelCapabilities.ResolveProvider(provider) switch
        {
            ProviderKind.Anthropic => new AnthropicModel(provider, apiKey, httpClient),
            ProviderKind.OpenAiResponses => new OpenAiResponsesModel(provider, apiKey, httpClient),
            _ => new OpenAiCompatibleModel(provider, apiKey, httpClient)
        };
}
