using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Workbench.Auth;
using Workbench.Core;
using Workbench.Desktop;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class ProviderSetupTests
{
    [Fact]
    public void FirstLaunchHasNoImplicitProviderAndEndpointHintsNeverUseKeys()
    {
        var settings = new DesktopSettings().NormalizeBackend();
        Assert.Equal("unconfigured", settings.Backend);
        Assert.Empty(settings.Provider.Endpoint);
        Assert.Empty(settings.Provider.Model);
        Assert.Null(settings.Provider.CredentialProfileId);
        Assert.Empty(settings.RememberProvider(settings.Provider).RecentProviders);
        Assert.Equal(ProviderKind.OpenAiResponses, ProviderCatalog.Detect("https://api.openai.com/v1")!.Provider);
        Assert.Equal("openrouter", ProviderCatalog.Detect("https://openrouter.ai/api/v1")!.Id);
        Assert.Null(ProviderCatalog.Detect("https://api.openai.com.attacker.invalid/v1"));
        Assert.Equal(ProviderKind.OpenAiCompatible, ProviderCredentialBinding.Family(new("https://custom.invalid/v1")));
        Assert.Equal(ProviderKind.OpenAiResponses, ModelCapabilities.ResolveProvider(new("https://api.openai.com/v1")));
        Assert.IsType<OpenAiResponsesModel>(AgentModelFactory.Create(new("https://api.openai.com/v1", "fixture"), "resolved-fixture-key"));
    }

    [Fact]
    public void ExistingConfiguredSettingsMigrateWithoutRequiringOnboardingAgain()
    {
        var old = DesktopSettings.FromJson("""{"backend":"api","provider":{"endpoint":"https://api.openai.com/v1","model":"existing-model","apiKeyEnvironmentVariable":"EXISTING_KEY"}}""");
        Assert.Equal("api", old.Backend);
        Assert.True(old.OnboardingCompleted);
        Assert.Equal("existing-model", old.Provider.Model);
        Assert.Equal("EXISTING_KEY", old.Provider.ApiKeyEnvironmentVariable);
        Assert.Equal(ProviderKind.OpenAiCompatible, old.Provider.Provider);
        var pending = DesktopSettings.FromJson(JsonSerializer.Serialize(new DesktopSettings(), Wire.Json));
        Assert.Equal("unconfigured", pending.Backend);
        Assert.False(pending.OnboardingCompleted);
    }

    [Fact]
    public async Task RealLoopbackMetadataRequestHasOnlySelectedEndpointAuthenticationAndNoInference()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = AcceptMetadataRequest(listener,
            """{"data":[{"id":"fixture-text","owned_by":"fixture"},{"id":"fixture-vision","name":"Vision","architecture":{"input_modalities":["text","image"]}}]}""");
        var result = await ProviderCatalog.ListAsync(new($"http://127.0.0.1:{port}/v1", Provider: ProviderKind.OpenAiCompatible), "fixture-local-key");
        var request = await accepted.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.StartsWith("GET /v1/models HTTP/", request);
        Assert.Contains("Authorization: Bearer fixture-local-key", request, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chat/completions", request);
        Assert.DoesNotContain("/responses", request);
        Assert.DoesNotContain("fixture-local-key", JsonSerializer.Serialize(result, Wire.Json));
        Assert.Null(result.Models[0].SupportsImages);
        Assert.True(result.Models[1].SupportsImages);
    }

    [Fact]
    public async Task AnthropicCatalogUsesNativeHeaderAndBoundedPagination()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new MetadataHandler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            Assert.Equal("fixture-native-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            Assert.Null(request.Headers.Authorization);
            return Json(requests.Count == 1
                ? """{"data":[{"id":"claude-fixture","display_name":"Fixture Claude"}],"has_more":true,"last_id":"claude-fixture"}"""
                : """{"data":[{"id":"claude-other","display_name":"Other"}],"has_more":false}""");
        }));
        var catalog = await ProviderCatalog.ListAsync(new("https://api.anthropic.com", Provider: ProviderKind.Anthropic), "fixture-native-key", client);
        Assert.Equal(new[] { "/v1/models", "/v1/models?after_id=claude-fixture" }, requests);
        Assert.Equal(2, catalog.Models.Count);
        Assert.False(catalog.Truncated);
    }

    [Fact]
    public async Task CatalogErrorsAndRedirectsNeverPersistServerBodyOrReplay()
    {
        var calls = 0;
        using var client = new HttpClient(new MetadataHandler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect) { Content = new StringContent("private-fixture-key") };
            response.Headers.Location = new("https://unselected.invalid/steal");
            return response;
        }));
        var error = await Assert.ThrowsAsync<ModelRequestException>(() => ProviderCatalog.ListAsync(new("https://selected.invalid/v1"), "private-fixture-key", client));
        Assert.DoesNotContain("private-fixture-key", error.ToString());
        Assert.DoesNotContain("unselected.invalid", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("secret", new ProviderCredentialSaveRequest { Provider = new(), ApiKey = "secret" }.ToString());
        Assert.DoesNotContain("secret", new ProviderModelsRequest { Provider = new(), ApiKey = "secret" }.ToString());
    }

    [Fact]
    public async Task NativeApiKeysAreEndpointBoundEncryptedAndSeparateFromAccountStorage()
    {
        if (!OperatingSystem.IsWindows()) return; // Other OS vault acceptance needs a live Keychain/Secret Service.
        var directory = Path.Combine(Path.GetTempPath(), "cogitide-api-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new ProviderSettings("https://api.example.invalid/v1", "fixture", Provider: ProviderKind.OpenAiCompatible);
            string id;
            using (var accountStore = new ProtectedAuthStore(directory))
            using (var store = new ApiCredentialStore(directory))
            {
                id = await store.SaveAsync(provider, "API_VERY_SECRET_FIXTURE_KEY");
                Assert.Equal("API_VERY_SECRET_FIXTURE_KEY", await store.ReadAsync(provider with { CredentialProfileId = id }));
                Assert.True((await store.StatusAsync(provider with { CredentialProfileId = id })).Configured);
                Assert.Throws<IOException>(() => new ApiCredentialStore(directory));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadAsync(provider with { Endpoint = "https://other.invalid/v1", CredentialProfileId = id }));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.ReadAsync(provider with { Provider = ProviderKind.OpenAiResponses, CredentialProfileId = id }));
                foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Where(file => !file.EndsWith(".lock", StringComparison.Ordinal)))
                    Assert.DoesNotContain("VERY_SECRET", Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)));
            }
            using var reopened = new ApiCredentialStore(directory);
            Assert.Equal("API_VERY_SECRET_FIXTURE_KEY", await reopened.ReadAsync(provider with { CredentialProfileId = id }));
            await reopened.DeleteAsync(provider with { CredentialProfileId = id });
            Assert.Null(await reopened.ReadAsync(provider with { CredentialProfileId = id }));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task NativeApiCredentialRotationPreservesItsBoundProfileWithoutConsumingAdditionalCapacity()
    {
        if (!OperatingSystem.IsWindows()) return; // Other OS vault acceptance needs a live Keychain/Secret Service.
        var directory = Path.Combine(Path.GetTempPath(), "cogitide-api-rotation-" + Guid.NewGuid().ToString("N"));
        var provider = new ProviderSettings("https://api.example.invalid/v1", "fixture", Provider: ProviderKind.OpenAiCompatible);
        string id;
        try
        {
            using (var store = new ApiCredentialStore(directory))
            {
                id = await store.SaveAsync(provider, "initial-fixture-key");
                provider = provider with { CredentialProfileId = id };
                for (var rotation = 0; rotation < 70; rotation++)
                    Assert.Equal(id, await store.SaveAsync(provider, "rotated-fixture-key-" + rotation));
                Assert.Equal("rotated-fixture-key-69", await store.ReadAsync(provider));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync(
                    provider with { Endpoint = "https://other.invalid/v1" }, "must-not-replace"));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.SaveAsync(
                    provider with { Provider = ProviderKind.OpenAiResponses }, "must-not-replace"));
                await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(
                    provider with { CredentialProfileId = "invalid-profile" }, "must-not-replace"));
                Assert.Equal("rotated-fixture-key-69", await store.ReadAsync(provider));

                // The rotated profile occupies exactly one of the 64 slots. Reaching the normal
                // capacity still permits replacement, and rejects only a new unrelated profile.
                for (var index = 1; index < 64; index++)
                    await store.SaveAsync(provider with { CredentialProfileId = null }, "other-fixture-key-" + index);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    store.SaveAsync(provider with { CredentialProfileId = null }, "over-capacity-fixture-key"));
                Assert.Equal(id, await store.SaveAsync(provider, "latest-fixture-key"));
            }
            using var reopened = new ApiCredentialStore(directory);
            Assert.Equal("latest-fixture-key", await reopened.ReadAsync(provider));
            await reopened.DeleteAsync(provider);
            Assert.Null(await reopened.ReadAsync(provider));
            // An explicit save after the referenced profile was removed creates a fresh binding.
            var replacementId = await reopened.SaveAsync(provider, "recreated-fixture-key");
            Assert.NotEqual(id, replacementId);
            Assert.Equal("recreated-fixture-key", await reopened.ReadAsync(provider with { CredentialProfileId = replacementId }));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("http://public.example/v1")]
    [InlineData("https://user:password@example.com/v1")]
    [InlineData("https://example.com/v1?key=secret")]
    [InlineData("https://example.com/v1#secret")]
    public void CredentialBindingsRejectInsecureOrEmbeddedSecretAddresses(string endpoint) =>
        Assert.Throws<ArgumentException>(() => ProviderCredentialBinding.Endpoint(endpoint));

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class MetadataHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private static async Task<string> AcceptMetadataRequest(TcpListener listener, string body)
    {
        using var connection = await listener.AcceptTcpClientAsync();
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
        var header = new StringBuilder();
        while (await reader.ReadLineAsync() is { Length: > 0 } line) header.AppendLine(line);
        var bytes = Encoding.UTF8.GetBytes(body);
        var prefix = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(prefix);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
        return header.ToString();
    }
}
