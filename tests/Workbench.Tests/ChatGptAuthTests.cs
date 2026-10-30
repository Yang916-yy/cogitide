using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Workbench.Auth;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class ChatGptAuthTests
{
    [Fact]
    public async Task DynamicRegistrationUsesPkceValidatesIdentityAndReusesClientAndHost()
    {
        using var fixture = new AuthFixture();
        await using var service = fixture.Service();
        var login = await service.LoginAsync();
        var query = HttpUtility.ParseQueryString(new Uri(login.AuthUrl!).Query);
        Assert.Equal("dynamic_agent_client", query["client_id"]);
        Assert.Equal("Cogitide", query["agent_name_hint"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.StartsWith("urn:uuid:", query["ext_agent_host_id"]);
        Assert.Equal("127.0.0.1", new Uri(query["redirect_uri"]!).Host);
        var state = await fixture.Finish(service, login);
        Assert.True(state.LoggedIn);
        Assert.Equal(ChatGptAccountService.Binding("subject-one", "oaiapp_fixture"), state.AccountBinding);
        Assert.Equal(query["redirect_uri"], fixture.LastGrant!["redirect_uri"]);
        Assert.Equal(query["code_challenge"], Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(fixture.LastGrant["code_verifier"]!))));
        Assert.Equal("oaiapp_fixture", fixture.LastGrant["client_id"]);
        var credential = await service.GetCredentialAsync();
        Assert.Equal("access-1", credential.AccessToken);
        Assert.DoesNotContain("access-1", credential.ToString());
        var again = await service.LoginAsync();
        var returning = HttpUtility.ParseQueryString(new Uri(again.AuthUrl!).Query);
        Assert.Equal("oaiapp_fixture", returning["client_id"]);
        Assert.Equal(query["ext_agent_host_id"], returning["ext_agent_host_id"]);
        Assert.Null(returning["agent_name_hint"]);
        Assert.NotNull(returning["id_token_hint"]);
        Assert.NotEqual(query["state"], returning["state"]);
        await service.CancelLoginAsync(again.LoginId);
    }

    [Fact]
    public async Task InvalidStateDoesNotRedeemCodeAndCancelledCallbackCannotCommit()
    {
        using var fixture = new AuthFixture();
        await using var service = fixture.Service();
        var login = await service.LoginAsync();
        var query = HttpUtility.ParseQueryString(new Uri(login.AuthUrl!).Query);
        using var client = new HttpClient();
        var wrong = await client.GetAsync(query["redirect_uri"] + "?code=secret-code&client_id=oaiapp_fixture&state=wrong");
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(0, fixture.TokenRequests);
        await service.CancelLoginAsync(login.LoginId);
        Assert.False((await service.GetAccountAsync()).LoggedIn);
        Assert.Empty(fixture.Store.Value!.Accounts);
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("expiry")]
    public async Task UntrustedIdentityCannotReplaceActiveAccount(string defect)
    {
        using var fixture = new AuthFixture();
        await using var service = fixture.Service();
        await fixture.Finish(service, await service.LoginAsync());
        var before = fixture.Store.Value;
        fixture.Defect = defect;
        var result = await fixture.Finish(service, await service.LoginAsync());
        Assert.NotNull(result.Error);
        Assert.True(result.LoggedIn); // the valid prior account remains active
        Assert.Equal(before, fixture.Store.Value);
    }

    [Fact]
    public async Task ReturningIdentityAndClientIdMustMatchOriginalRegistration()
    {
        using var fixture = new AuthFixture();
        await using var service = fixture.Service();
        await fixture.Finish(service, await service.LoginAsync());
        var before = fixture.Store.Value;
        fixture.Subject = "subject-two";
        var result = await fixture.Finish(service, await service.LoginAsync());
        Assert.Contains("does not match", result.Error);
        Assert.Equal(before, fixture.Store.Value);
        var requests = fixture.TokenRequests;
        var mismatch = await fixture.Finish(service, await service.LoginAsync(), "oaiapp_different");
        Assert.Contains("client registration", mismatch.Error);
        Assert.Equal(requests, fixture.TokenRequests);
        Assert.Equal(before, fixture.Store.Value);
    }

    [Fact]
    public async Task MissingPlanPermissionRetainsRegistrationButNeverProvidesInferenceCredential()
    {
        using var fixture = new AuthFixture { Scopes = "openid profile email offline_access" };
        await using var service = fixture.Service();
        var result = await fixture.Finish(service, await service.LoginAsync());
        Assert.False(result.LoggedIn);
        Assert.NotNull(result.AccountBinding);
        Assert.Single(await service.ListAccountsAsync());
        await Assert.ThrowsAsync<ChatGptAuthException>(() => service.GetCredentialAsync());
        var again = await service.LoginAsync();
        Assert.Equal("oaiapp_fixture", HttpUtility.ParseQueryString(new Uri(again.AuthUrl!).Query)["client_id"]);
        await service.CancelLoginAsync(again.LoginId);
    }

    [Fact]
    public async Task ConcurrentRefreshCommitsOneRotatedCredentialAndScopesTogether()
    {
        using var fixture = new AuthFixture { Seconds = 30 };
        await using var service = fixture.Service();
        await fixture.Finish(service, await service.LoginAsync());
        fixture.Seconds = 3600;
        var credentials = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.GetCredentialAsync()));
        Assert.Equal(2, fixture.TokenRequests);
        Assert.All(credentials, credential => Assert.Equal("access-2", credential.AccessToken));
        Assert.Equal("refresh-1", fixture.LastGrant!["refresh_token"]);
        Assert.Null(fixture.LastGrant["scope"]);
        var account = Assert.Single(fixture.Store.Value!.Accounts);
        Assert.Equal("access-2", account.AccessToken);
        Assert.Equal("refresh-2", account.RefreshToken);
        Assert.Contains("chatgpt.tokens.use.direct", account.Scopes);
    }

    [Fact]
    public async Task RefreshRejectsAccountSwapAndDoesNotExposeFailedReplacement()
    {
        using var fixture = new AuthFixture { Seconds = 30 };
        await using var service = fixture.Service();
        await fixture.Finish(service, await service.LoginAsync());
        var before = fixture.Store.Value;
        fixture.Subject = "unexpected-subject";
        await Assert.ThrowsAsync<ChatGptAuthException>(() => service.GetCredentialAsync());
        Assert.Equal(before, fixture.Store.Value);
    }

    [Fact]
    public async Task RefreshScopeLossIsSavedButNeverReturnedForInference()
    {
        using var fixture = new AuthFixture { Seconds = 30 };
        await using var service = fixture.Service();
        await fixture.Finish(service, await service.LoginAsync());
        fixture.Scopes = "openid profile email offline_access";
        await Assert.ThrowsAsync<ChatGptAuthException>(() => service.GetCredentialAsync());
        Assert.Equal("refresh-2", Assert.Single(fixture.Store.Value!.Accounts).RefreshToken);
        Assert.False((await service.GetAccountAsync()).LoggedIn);
    }

    [Fact]
    public async Task LogoutRevokesAndClearsTokensWhilePreservingClientAndHost()
    {
        using var fixture = new AuthFixture();
        await using var service = fixture.Service();
        await fixture.Finish(service, await service.LoginAsync());
        var host = fixture.Store.Value!.HostId;
        await service.LogoutAsync();
        Assert.Equal(1, fixture.Revocations);
        Assert.Equal("refresh-1", fixture.LastRevocation!["token"]);
        var saved = Assert.Single(fixture.Store.Value.Accounts);
        Assert.Null(saved.AccessToken); Assert.Null(saved.RefreshToken); Assert.Null(saved.IdToken);
        Assert.Equal("oaiapp_fixture", saved.ClientId); Assert.Equal(host, fixture.Store.Value.HostId);
        Assert.False((await service.GetAccountAsync()).LoggedIn);
        var login = await service.LoginAsync();
        var query = HttpUtility.ParseQueryString(new Uri(login.AuthUrl!).Query);
        Assert.Equal("oaiapp_fixture", query["client_id"]); Assert.Null(query["id_token_hint"]);
        await service.CancelLoginAsync(login.LoginId);
    }

    [Fact]
    public async Task RemoteLogoutFailureIsReportedWhileLocalTokensAreCleared()
    {
        using var fixture = new AuthFixture { RevokeStatus = HttpStatusCode.ServiceUnavailable };
        await using var service = fixture.Service();
        await fixture.Finish(service, await service.LoginAsync());
        AccountState? update = null; service.AccountChanged += value => update = value;
        await service.LogoutAsync();
        Assert.False(update!.LoggedIn); Assert.Contains("not confirmed", update.Error);
        Assert.Equal(2, fixture.Revocations);
        Assert.Null(Assert.Single(fixture.Store.Value!.Accounts).RefreshToken);
    }

    [Fact]
    public async Task AccountPickerKeepsSameEmailRegistrationsSeparateAndCatalogIsLive()
    {
        using var fixture = new AuthFixture();
        await using var service = fixture.Service();
        var first = await fixture.Finish(service, await service.LoginAsync());
        var second = await fixture.Finish(service, await service.LoginAsync(true), "oaiapp_workspace2");
        Assert.NotEqual(first.AccountBinding, second.AccountBinding);
        var accounts = await service.ListAccountsAsync();
        Assert.Equal(2, accounts.Count); Assert.Single(accounts, a => a.IsSelected);
        await service.SelectAccountAsync(first.AccountBinding!);
        var models = await service.ListModelsAsync();
        Assert.Equal(["model-one", "model-two"], models.Select(m => m.Id));
        Assert.Equal(["low", "high"], models[0].ReasoningEfforts);
        Assert.True(models[0].SupportsImages); Assert.Null(models[1].SupportsImages);
        Assert.Equal("access-1", fixture.ModelBearer);
    }

    [Fact]
    public async Task UpstreamErrorBodyNeverEscapesAuthService()
    {
        using var fixture = new AuthFixture { TokenStatus = HttpStatusCode.BadRequest };
        await using var service = fixture.Service();
        var result = await fixture.Finish(service, await service.LoginAsync());
        Assert.Contains("HTTP 400", result.Error);
        Assert.DoesNotContain("UPSTREAM_SECRET", result.Error);
        Assert.Empty(fixture.Store.Value!.Accounts);
    }

    [Fact]
    public async Task FailedCodeExchangeRetainsOnlyIssuedClientAndRetriesWithFreshAuthorization()
    {
        using var fixture = new AuthFixture { TokenStatus = HttpStatusCode.BadRequest };
        await using var service = fixture.Service();
        var initial = await service.LoginAsync();
        var initialQuery = HttpUtility.ParseQueryString(new Uri(initial.AuthUrl!).Query);
        await fixture.Finish(service, initial);
        Assert.Empty(fixture.Store.Value!.Accounts);
        Assert.Equal("oaiapp_fixture", fixture.Store.Value.PendingClientId);
        await Assert.ThrowsAsync<ChatGptAuthException>(() => service.GetCredentialAsync());
        var retry = await service.LoginAsync();
        var query = HttpUtility.ParseQueryString(new Uri(retry.AuthUrl!).Query);
        Assert.Equal("oaiapp_fixture", query["client_id"]);
        Assert.Null(query["agent_name_hint"]);
        Assert.NotEqual(initialQuery["state"], query["state"]);
        Assert.NotEqual(initialQuery["code_challenge"], query["code_challenge"]);
        fixture.TokenStatus = HttpStatusCode.OK;
        Assert.True((await fixture.Finish(service, retry)).LoggedIn);
        Assert.Null(fixture.Store.Value.PendingClientId);
    }

    [Fact]
    public async Task ExplicitNewAccountDoesNotReuseUnvalidatedPendingRegistration()
    {
        using var fixture = new AuthFixture { TokenStatus = HttpStatusCode.BadRequest };
        await using var service = fixture.Service();
        await fixture.Finish(service, await service.LoginAsync());
        var fresh = await service.LoginAsync(true);
        Assert.Equal("dynamic_agent_client", HttpUtility.ParseQueryString(new Uri(fresh.AuthUrl!).Query)["client_id"]);
        Assert.Null(fixture.Store.Value!.PendingClientId);
        await service.CancelLoginAsync(fresh.LoginId);
    }

    [Fact]
    public void NativeVaultPersistsNoPlaintextAndLocksAgainstConcurrentRefreshOwners()
    {
        if (!OperatingSystem.IsWindows()) return; // macOS/Linux vaults require their actual desktop secret service.
        var directory = Path.Combine(Path.GetTempPath(), "cogito-auth-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var account = new AccountRegistration(ChatGptAccountService.Binding("sub", "client"), "sub", "client", "test@example.invalid",
                "VERY_SECRET_ACCESS", "VERY_SECRET_REFRESH", "VERY_SECRET_ID", ["openid"], DateTimeOffset.UtcNow);
            var state = new AuthState(1, "urn:uuid:" + Guid.NewGuid(), account.AccountBinding, [account]);
            using (var store = new ProtectedAuthStore(directory))
            {
                store.Write(state);
                Assert.Equal("VERY_SECRET_ACCESS", Assert.Single(store.Read()!.Accounts).AccessToken);
                Assert.Throws<IOException>(() => new ProtectedAuthStore(directory));
                foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories).Where(file => Path.GetFileName(file) != "auth.lock"))
                    Assert.DoesNotContain("VERY_SECRET", Encoding.UTF8.GetString(File.ReadAllBytes(file)));
            }
            using var reopened = new ProtectedAuthStore(directory);
            Assert.Equal(account.AccountBinding, reopened.Read()!.Selected);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    internal sealed class MemoryStore : IAuthStore
    {
        internal AuthState? Value;
        public AuthState? Read() => Value;
        public void Write(AuthState state) => Value = state;
        public void Dispose() { }
    }

    internal sealed class AuthFixture : HttpMessageHandler
    {
        internal readonly MemoryStore Store = new();
        private readonly RSA _rsa = RSA.Create(2048);
        internal string Subject = "subject-one";
        internal string Scopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
        internal string? Nonce, Defect, ModelBearer;
        internal int Seconds = 3600, TokenRequests, Revocations;
        internal HttpStatusCode TokenStatus = HttpStatusCode.OK, RevokeStatus = HttpStatusCode.OK;
        internal System.Collections.Specialized.NameValueCollection? LastGrant, LastRevocation;
        private HttpClient? _client;
        internal ChatGptAccountService Service() => new(Store, _client = new HttpClient(this), AuthEndpoints.Official);
        internal async Task<AccountState> Finish(ChatGptAccountService service, LoginInfo login, string clientId = "oaiapp_fixture")
        {
            var completion = new TaskCompletionSource<AccountState>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(AccountState state) => completion.TrySetResult(state);
            service.AccountChanged += Changed;
            try
            {
                var query = HttpUtility.ParseQueryString(new Uri(login.AuthUrl!).Query);
                Nonce = query["nonce"];
                using var http = new HttpClient();
                using var response = await http.GetAsync(query["redirect_uri"] + "?code=fixture-code&state=" + Uri.EscapeDataString(query["state"]!) + "&client_id=" + clientId);
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally { service.AccountChanged -= Changed; }
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("openid-configuration", StringComparison.Ordinal))
                return Json(new { issuer = "https://auth.openai.com", jwks_uri = "https://auth.openai.com/jwks", revocation_endpoint = "https://auth.openai.com/revoke" });
            if (path == "/jwks")
            {
                var key = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa) { KeyId = "fixture-key" });
                return Json(new { keys = new[] { new { kty = key.Kty, n = key.N, e = key.E, kid = key.Kid, use = "sig", alg = "RS256" } } });
            }
            if (path.EndsWith("/oauth/token", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref TokenRequests);
                LastGrant = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (TokenStatus != HttpStatusCode.OK) return new(TokenStatus) { Content = new StringContent("UPSTREAM_SECRET") };
                await Task.Delay(10, cancellationToken);
                using var wrong = RSA.Create(2048);
                var key = new RsaSecurityKey(Defect == "signature" ? wrong : _rsa) { KeyId = "fixture-key" };
                var descriptor = new SecurityTokenDescriptor
                {
                    Issuer = Defect == "issuer" ? "https://wrong.invalid" : "https://auth.openai.com",
                    Audience = Defect == "audience" ? "wrong-client" : LastGrant["client_id"],
                    Claims = new Dictionary<string, object> { ["sub"] = Subject, ["email"] = "same@example.invalid", ["nonce"] = Defect == "nonce" ? "wrong" : Nonce! },
                    NotBefore = DateTime.UtcNow.AddMinutes(-10), IssuedAt = DateTime.UtcNow.AddMinutes(-10),
                    Expires = DateTime.UtcNow.AddMinutes(Defect == "expiry" ? -5 : 30), SigningCredentials = new(key, SecurityAlgorithms.RsaSha256)
                };
                return Json(new { access_token = "access-" + TokenRequests, refresh_token = "refresh-" + TokenRequests,
                    id_token = new JsonWebTokenHandler().CreateToken(descriptor), token_type = "Bearer", expires_in = Seconds, scope = Scopes });
            }
            if (path == "/revoke")
            {
                Revocations++; LastRevocation = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                return new(RevokeStatus);
            }
            if (path == "/v1/models")
            {
                ModelBearer = request.Headers.Authorization?.Parameter;
                return Json(new { models = new object[]
                {
                    new { slug = "model-one", display_name = "Model One", visibility = "list", input_modalities = new[] { "text", "image" }, supported_reasoning_levels = new[] { new { effort = "low" }, new { effort = "high" } }, default_reasoning_level = "low" },
                    new { slug = "hidden", display_name = "Hidden", visibility = "hide" },
                    new { slug = "model-two", display_name = "Model Two", visibility = "list" }
                } });
            }
            throw new InvalidOperationException("Unexpected auth fixture endpoint.");
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        protected override void Dispose(bool disposing) { if (disposing) _rsa.Dispose(); base.Dispose(disposing); }
    }
}
