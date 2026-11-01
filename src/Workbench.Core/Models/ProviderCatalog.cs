using System.Net.Http.Headers;
using System.Text.Json;
using Workbench.Protocol;

namespace Workbench.Core;

/// <summary>Explicit metadata requests over the existing BCL provider transport. Never performs inference.</summary>
public static class ProviderCatalog
{
    public static IReadOnlyList<ProviderPreset> Presets { get; } = Array.AsReadOnly(new[]
    {
        new ProviderPreset("openai", "OpenAI", "https://api.openai.com/v1", ProviderKind.OpenAiResponses, "OPENAI_API_KEY"),
        new ProviderPreset("anthropic", "Anthropic / Claude", "https://api.anthropic.com", ProviderKind.Anthropic, "ANTHROPIC_API_KEY"),
        new ProviderPreset("deepseek", "DeepSeek", "https://api.deepseek.com", ProviderKind.DeepSeek, "DEEPSEEK_API_KEY"),
        new ProviderPreset("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", ProviderKind.OpenAiCompatible, "OPENROUTER_API_KEY"),
        new ProviderPreset("mistral", "Mistral", "https://api.mistral.ai/v1", ProviderKind.OpenAiCompatible, "MISTRAL_API_KEY")
    });

    /// <summary>Address-based hint only. API key prefixes cannot identify an arbitrary service reliably.</summary>
    public static ProviderPreset? Detect(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var candidate)) return null;
        return Presets.FirstOrDefault(p => new Uri(p.Endpoint).Host.Equals(candidate.Host, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<ProviderModelCatalog> ListAsync(ProviderSettings provider, string apiKey,
        HttpClient? httpClient = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8192 || apiKey.Any(char.IsControl))
            throw new ArgumentException("Provide a valid API key before requesting model metadata.");
        var endpoint = ProviderCredentialBinding.Endpoint(provider.Endpoint);
        var family = ProviderCredentialBinding.Family(provider);
        var baseUri = family == ProviderKind.Anthropic && !endpoint.EndsWith("/v1", StringComparison.Ordinal)
            ? endpoint + "/v1/models" : endpoint + "/models";
        var client = httpClient ?? ModelHttpTransport.SharedClient;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        List<ModelInfo> models = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> cursors = new(StringComparer.Ordinal);
        var truncated = false;
        string? after = null;
        try
        {
            for (var page = 0; page < 4; page++)
            {
                var requestUri = after is null ? baseUri : baseUri + "?after_id=" + Uri.EscapeDataString(after);
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                if (family == ProviderKind.Anthropic)
                {
                    request.Headers.Add("x-api-key", apiKey);
                    request.Headers.Add("anthropic-version", "2023-06-01");
                }
                else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (!response.IsSuccessStatusCode) throw ModelRequestException.ForStatus((int)response.StatusCode);
                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                var bytes = new byte[2 * 1024 * 1024 + 1];
                var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, deadline.Token);
                if (count >= bytes.Length) throw new InvalidDataException("Provider model catalog exceeds 2 MiB.");
                using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Provider did not return a supported model catalog. Enter a model ID manually.");
                foreach (var item in data.EnumerateArray())
                {
                    if (models.Count >= 1000) { truncated = true; break; }
                    if (!item.TryGetProperty("id", out var idProperty) || idProperty.ValueKind != JsonValueKind.String) continue;
                    var id = idProperty.GetString()!;
                    if (id.Length is < 1 or > 256 || id.Any(char.IsControl) || !ids.Add(id)) continue;
                    var name = item.TryGetProperty("display_name", out var display) && display.ValueKind == JsonValueKind.String ? display.GetString() :
                        item.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : id;
                    name = new string((name ?? id).Where(c => !char.IsControl(c)).Take(512).ToArray());
                    bool? images = null;
                    // Only explicit service metadata determines image support. Model-name heuristics are not evidence.
                    if (item.TryGetProperty("input_modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Array)
                        images = modalities.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "image");
                    else if (item.TryGetProperty("architecture", out var architecture) && architecture.ValueKind == JsonValueKind.Object &&
                        architecture.TryGetProperty("input_modalities", out modalities) && modalities.ValueKind == JsonValueKind.Array)
                        images = modalities.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "image");
                    else if (item.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object &&
                        capabilities.TryGetProperty("vision", out var vision) && vision.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        images = vision.GetBoolean();
                    if (images is null && item.TryGetProperty("capabilities", out var nativeCapabilities) && nativeCapabilities.ValueKind == JsonValueKind.Object &&
                        nativeCapabilities.TryGetProperty("image_input", out var imageInput) && imageInput.ValueKind == JsonValueKind.Object &&
                        imageInput.TryGetProperty("supported", out var supported) && supported.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        images = supported.GetBoolean();
                    string[]? efforts = null;
                    string? defaultEffort = null;
                    if (item.TryGetProperty("effort", out var effort) && effort.ValueKind == JsonValueKind.Object)
                    {
                        if (effort.TryGetProperty("supported_levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                            efforts = levels.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)
                                .Where(value => value.Length <= 32 && !value.Any(char.IsControl)).Take(16).ToArray();
                        if (effort.TryGetProperty("default_level", out var preferred) && preferred.ValueKind == JsonValueKind.String)
                            defaultEffort = preferred.GetString();
                    }
                    models.Add(new(id, name, efforts, defaultEffort, images));
                }
                var more = root.TryGetProperty("has_more", out var hasMore) && hasMore.ValueKind == JsonValueKind.True;
                if (!more || truncated) break;
                if (family != ProviderKind.Anthropic || !root.TryGetProperty("last_id", out var last) || last.ValueKind != JsonValueKind.String ||
                    string.IsNullOrEmpty(after = last.GetString()) || after.Length > 256 || !cursors.Add(after))
                { truncated = true; break; }
                if (page == 3) truncated = true;
            }
            return new(models, truncated, "provider-metadata");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new ModelRequestException("Provider catalog request timed out. No inference request was made."); }
        catch (HttpRequestException)
        { throw new ModelRequestException("Provider catalog could not be reached. Check the selected endpoint and network connection."); }
        catch (JsonException)
        { throw new ModelRequestException("Provider returned an invalid model catalog. Enter a model ID manually."); }
    }
}
