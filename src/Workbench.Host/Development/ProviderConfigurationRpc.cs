using System.Text.Json;
using Workbench.Auth;
using Workbench.Core;
using Workbench.Protocol;

internal sealed partial class WorkbenchHost
{
    // Initialized by the main constructor. Never shares the OAuth store's lifetime lock or credential namespace.
    private string _providerDataDirectory = "";
    private readonly object _providerCredentialGate = new();
    private ApiCredentialStore? _apiCredentials;
    private ApiCredentialStore ApiCredentials
    {
        get
        {
            lock (_providerCredentialGate)
            {
                if (string.IsNullOrWhiteSpace(_providerDataDirectory)) throw new InvalidOperationException("Provider credential storage was not initialized by this Host.");
                return _apiCredentials ??= new ApiCredentialStore(_providerDataDirectory);
            }
        }
    }
    private void DisposeProviderConfiguration()
    {
        lock (_providerCredentialGate) { _apiCredentials?.Dispose(); _apiCredentials = null; }
    }
    private async Task<string> ResolveProviderApiKeyAsync(ProviderSettings provider, CancellationToken ct)
    {
        if (provider.CredentialProfileId is not null)
            return await ApiCredentials.ReadAsync(provider, ct)
                ?? throw new InvalidOperationException("The selected API credential is unavailable on this Host. Open Connections to save a credential for this endpoint.");
        var key = string.IsNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable) ? null : Environment.GetEnvironmentVariable(provider.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Open Connections to save an API key for this endpoint, or configure its environment variable on this Host.");
        ApiCredentialStore.ValidateKey(key);
        return key;
    }
    private async Task<object> DispatchProviderConfigurationAsync(string method, JsonElement p, CancellationToken ct)
    {
        if (method == "provider.presets") return ProviderCatalog.Presets;
        var provider = p.TryGetProperty("provider", out var element) ? element.Deserialize<ProviderSettings>(Wire.Json) : null;
        if (provider is null) throw new ArgumentException("Provider configuration is required.");
        _ = ProviderCredentialBinding.Endpoint(provider.Endpoint);
        switch (method)
        {
            case "provider.credential.status":
                return provider.CredentialProfileId is not null ? await ApiCredentials.StatusAsync(provider, ct)
                    : new ProviderCredentialStatus(!string.IsNullOrWhiteSpace(provider.ApiKeyEnvironmentVariable) &&
                        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(provider.ApiKeyEnvironmentVariable)));
            case "provider.credential.save":
            {
                var key = p.TryGetProperty("apiKey", out var raw) && raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
                if (key is null) throw new ArgumentException("An API key is required.");
                var id = await ApiCredentials.SaveAsync(provider, key, ct);
                return new ProviderCredentialStatus(true, id);
            }
            case "provider.credential.delete":
                await ApiCredentials.DeleteAsync(provider, ct);
                return new ProviderCredentialStatus(false);
            case "provider.models":
            {
                // An unsaved masked GUI key may be used transiently for this explicit metadata request only.
                var key = p.TryGetProperty("apiKey", out var raw) && raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
                key = string.IsNullOrEmpty(key) ? await ResolveProviderApiKeyAsync(provider, ct) : key;
                return await ProviderCatalog.ListAsync(provider, key, _modelHttpClient, ct);
            }
            default: throw new ArgumentException("Unknown provider configuration request.");
        }
    }
}
