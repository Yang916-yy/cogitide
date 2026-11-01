using Workbench.Protocol;

namespace Workbench.Core;

/// <summary>Explicit provider capabilities and shared preflight checks, independent of SDK transports.</summary>
public static class ModelCapabilities
{
    public static ProviderKind ResolveProvider(ProviderSettings settings)
    {
        if (!Enum.IsDefined(settings.Provider)) throw new ArgumentException("Unknown model provider.");
        var endpoint = new Uri(NormalizeEndpoint(settings.Endpoint));
        if (settings.Provider != ProviderKind.Auto) return settings.Provider;
        return endpoint.Host.ToLowerInvariant() switch
        {
            "api.deepseek.com" => ProviderKind.DeepSeek,
            "api.anthropic.com" => ProviderKind.Anthropic,
            "api.openai.com" => ProviderKind.OpenAiResponses,
            _ => ProviderKind.OpenAiCompatible
        };
    }

    public static string NormalizeEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new ArgumentException("Model endpoint must use HTTPS (HTTP is allowed only for a local model).");
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Model endpoint must not contain credentials, a query string or a fragment.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    public static void ValidateContinuation(ModelContinuationState state, ProviderSettings settings, string expectedFormat)
    {
        if (state.Format != expectedFormat || state.Model != settings.Model ||
            !string.Equals(NormalizeEndpoint(state.Endpoint), NormalizeEndpoint(settings.Endpoint), StringComparison.Ordinal))
            throw new InvalidDataException("Stored model continuation belongs to a different endpoint, model or protocol. Start a new chat to change providers.");
    }

    public static bool CanUseImages(ProviderSettings settings) => ResolveProvider(settings) switch
    {
        ProviderKind.Anthropic => true,
        ProviderKind.OpenAiResponses => settings.SupportsImages,
        ProviderKind.DeepSeek => settings.Model is "deepseek-flash" or "deepseek-v4-flash" or "deepseek-v4-flash-vision-exp",
        _ => settings.SupportsImages
    };

    public static void ValidateImages(IReadOnlyList<ChatMessage> messages, ProviderSettings settings)
    {
        long totalBytes = 0;
        var count = 0;
        foreach (var message in messages)
        {
            if (message.Images is not { Count: > 0 }) continue;
            if (!CanUseImages(settings)) throw new InvalidDataException("The selected model has no enabled image input capability.");
            if (message.Role != "user") throw new InvalidDataException("Image attachments are supported only in user messages.");
            if (message.Images.Count > ImageInputs.MaxCount - count) throw new InvalidDataException("Too many images in the model request.");
            count += message.Images.Count;
            ImageInputs.Validate(message.Images);
            foreach (var image in message.Images)
            {
                // The shared protocol validator checked the format. BCL span sizing avoids decoding a second time here.
                System.Buffers.Text.Base64.IsValid(image.Base64Data.AsSpan(), out var bytes);
                if ((totalBytes += bytes) > ImageInputs.MaxTotalBytes)
                    throw new InvalidDataException("Image attachments exceed the model request size limit.");
            }
        }
    }
}
