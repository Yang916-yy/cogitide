using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Workbench.Protocol;

namespace Workbench.Auth;

internal sealed record AuthEndpoints(Uri Issuer, Uri Authorize, Uri Token, Uri Discovery, Uri Models)
{
    internal static AuthEndpoints Official { get; } = new(new("https://auth.openai.com"),
        new("https://auth.openai.com/api/accounts/authorize"), new("https://auth.openai.com/api/accounts/oauth/token"),
        new("https://auth.openai.com/.well-known/openid-configuration"), new("https://api.openai.com/v1/models"));
}

/// <summary>Official dynamic-registration OAuth, deliberately independent of any agent execution backend.</summary>
public sealed class ChatGptAccountService : IChatGptAccountService
{
    private const string Resource = "https://api.openai.com/v1";
    private const string RequestedScopes = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
    private readonly IAuthStore _store;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly AuthEndpoints _endpoints;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private AuthState? _state;
    private PendingLogin? _pending;
    private Task? _loginTask;
    private JsonWebKeySet? _keys;
    private DateTimeOffset _keysFetched;
    private Uri? _revocation;
    private bool _disposed;
    public event Action<AccountState>? AccountChanged;

    public ChatGptAccountService(string directory) : this(new ProtectedAuthStore(directory), CreateHttp(), AuthEndpoints.Official, TimeProvider.System, true) { }
    internal ChatGptAccountService(IAuthStore store, HttpClient http, AuthEndpoints endpoints, TimeProvider? time = null, bool ownsHttp = false)
    { _store = store; _http = http; _endpoints = endpoints; _time = time ?? TimeProvider.System; _ownsHttp = ownsHttp; }
    private static HttpClient CreateHttp() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(20),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5), MaxResponseHeadersLength = 32,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    }) { Timeout = TimeSpan.FromSeconds(30) };

    private AuthState State
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state is not null) return _state;
            var loaded = _store.Read();
            if (loaded is null)
            {
                loaded = new(1, "urn:uuid:" + Guid.NewGuid().ToString("D"), null, []);
                _store.Write(loaded);
            }
            if (loaded.Version != 1 || !loaded.HostId.StartsWith("urn:uuid:", StringComparison.Ordinal) ||
                !Guid.TryParse(loaded.HostId[9..], out _) || loaded.Accounts is null || loaded.Accounts.Count > 32 ||
                loaded.Accounts.Select(a => a.AccountBinding).Distinct(StringComparer.Ordinal).Count() != loaded.Accounts.Count ||
                loaded.Accounts.Any(a => a.AccountBinding != Binding(a.Subject, a.ClientId) || a.ClientId == "dynamic_agent_client") ||
                (loaded.PendingClientId is { } pending && (pending.Length is < 1 or > 256 || pending == "dynamic_agent_client")) ||
                (loaded.Selected is not null && !loaded.Accounts.Any(a => a.AccountBinding == loaded.Selected)))
                throw new ChatGptAuthException("Saved ChatGPT registrations could not be validated. The protected record was preserved.");
            return _state = loaded;
        }
    }
    private AccountRegistration? Selected => State.Accounts.FirstOrDefault(a => a.AccountBinding == State.Selected);
    private static bool Authorized(AccountRegistration account) => account.AccessToken is { Length: > 0 } &&
        account.Scopes.Contains("chatgpt.tokens.use.direct", StringComparer.Ordinal) && account.Scopes.Contains("resource.invoke", StringComparer.Ordinal);
    private AccountState Describe(string? error = null)
    {
        var selected = Selected;
        return new(true, selected is not null && Authorized(selected) &&
            (selected.ExpiresAt > _time.GetUtcNow() || selected.RefreshToken is { Length: > 0 }),
            selected?.Email, Error: error, AccountBinding: selected?.AccountBinding);
    }
    private void Publish(AccountState state) { try { AccountChanged?.Invoke(state); } catch (Exception) { } }
    private void Commit(AuthState state) { _store.Write(state); _state = state; }

    public async Task<AccountState> GetAccountAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return Describe(); }
        catch (ChatGptAuthException ex) { return new(false, false, Error: ex.Message); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AccountProfile>> ListAccountsAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return State.Accounts.Select(a => new AccountProfile(a.AccountBinding, a.Email,
            "ChatGPT · " + a.AccountBinding[..8], a.AccountBinding == State.Selected,
            Authorized(a) && (a.ExpiresAt > _time.GetUtcNow() || a.RefreshToken is { Length: > 0 }))).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<AccountState> SelectAccountAsync(string accountBinding, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!State.Accounts.Any(a => a.AccountBinding == accountBinding)) throw new ChatGptAuthException("The saved ChatGPT account was not found.");
            CancelPending();
            Commit(State with { Selected = accountBinding, PendingClientId = null });
            var result = Describe(); Publish(result); return result;
        }
        finally { _gate.Release(); }
    }

    public Task<LoginInfo> LoginAsync(CancellationToken cancellationToken = default) => LoginAsync(false, cancellationToken);
    public async Task<LoginInfo> LoginAsync(bool newAccount, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = State; // persists the stable host ID before opening authorization
            if (newAccount && state.Accounts.Count >= 32) throw new ChatGptAuthException("The saved account limit was reached.");
            CancelPending();
            if (newAccount && state.PendingClientId is not null) { Commit(state with { PendingClientId = null }); state = State; }
            var unfinishedClient = newAccount ? null : state.PendingClientId;
            var selected = newAccount || unfinishedClient is not null ? null : Selected;
            var pending = new PendingLogin(selected, unfinishedClient, _lifetime.Token);
            var query = new Dictionary<string, string>
            {
                ["client_id"] = pending.ClientId ?? "dynamic_agent_client", ["ext_agent_host_id"] = state.HostId,
                ["response_type"] = "code", ["redirect_uri"] = pending.RedirectUri.AbsoluteUri,
                ["scope"] = RequestedScopes, ["resource"] = Resource, ["state"] = pending.State,
                ["nonce"] = pending.Nonce, ["code_challenge_method"] = "S256",
                ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(pending.Verifier)))
            };
            if (pending.ClientId is null) query["agent_name_hint"] = "Cogitide";
            else if (selected is not null)
            {
                if (selected.IdToken is { Length: > 0 } hint) query["id_token_hint"] = hint;
                if (selected.Email is { Length: > 0 } email) query["login_hint"] = email;
            }
            _pending = pending;
            _loginTask = CompleteLoginAsync(pending);
            return new(BuildUri(_endpoints.Authorize, query), pending.Id);
        }
        finally { _gate.Release(); }
    }

    public async Task CancelLoginAsync(string? loginId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_pending is not null && loginId is not null && loginId != _pending.Id)
                throw new ChatGptAuthException("This login attempt is no longer active.");
            CancelPending();
        }
        finally { _gate.Release(); }
    }
    private void CancelPending() { var pending = _pending; _pending = null; pending?.Dispose(); }

    private async Task CompleteLoginAsync(PendingLogin pending)
    {
        try
        {
            string? code = null, clientId = null;
            while (code is null)
            {
                var context = await pending.Listener.GetContextAsync().WaitAsync(pending.Token);
                try
                {
                    var request = context.Request;
                    if (request.HttpMethod != "GET" || request.Url?.AbsolutePath != "/auth/callback" ||
                        request.Url.Host != "127.0.0.1" || request.Url.Port != pending.RedirectUri.Port ||
                        request.RemoteEndPoint is null || !IPAddress.IsLoopback(request.RemoteEndPoint.Address) ||
                        request.Url.Query.Length > 16_384)
                    { await ReplyAsync(context, 400, "Invalid callback.", pending.Token); continue; }
                    var query = request.QueryString;
                    if (query.GetValues("state") is not { Length: 1 } states || !Equal(states[0], pending.State))
                    { await ReplyAsync(context, 400, "Invalid login state.", pending.Token); continue; }
                    if (query["error"] is not null)
                    { await ReplyAsync(context, 400, "Authorization was not completed. Return to Cogitide.", pending.Token); throw new ChatGptAuthException("ChatGPT authorization was denied or interrupted."); }
                    if (query.GetValues("code") is not { Length: 1 } codes || string.IsNullOrWhiteSpace(codes[0]) || codes[0].Length > 8192)
                    { await ReplyAsync(context, 400, "Missing authorization code.", pending.Token); throw new ChatGptAuthException("The authorization callback was incomplete."); }
                    var ids = query.GetValues("client_id");
                    if (ids is { Length: > 1 }) throw new ChatGptAuthException("The authorization callback contained conflicting client registrations.");
                    clientId = ids?.SingleOrDefault() ?? pending.ClientId;
                    if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 256 || clientId == "dynamic_agent_client" ||
                        (pending.ClientId is not null && clientId != pending.ClientId))
                        throw new ChatGptAuthException("The authorization callback did not match the requested client registration.");
                    code = codes[0];
                    await ReplyAsync(context, 200, "Authorization received. You may close this tab and return to Cogitide.", pending.Token);
                }
                finally { context.Response.Close(); }
            }
            pending.Listener.Close();
            if (pending.Previous is null)
            {
                // A validated callback proves which registration was issued, not the account identity.
                // Retain only that client ID so an invalid_grant/unknown network result can use fresh authorization.
                await _gate.WaitAsync(pending.Token);
                try
                {
                    if (_pending != pending) return;
                    Commit(State with { PendingClientId = clientId });
                }
                finally { _gate.Release(); }
            }
            using var token = await PostAsync(_endpoints.Token, new()
            {
                ["grant_type"] = "authorization_code", ["client_id"] = clientId!, ["code"] = code,
                ["code_verifier"] = pending.Verifier, ["redirect_uri"] = pending.RedirectUri.AbsoluteUri, ["resource"] = Resource
            }, pending.Token);
            var registration = await RegistrationAsync(token.RootElement, clientId!, pending.Previous, pending.Nonce, pending.Token);
            await _gate.WaitAsync(pending.Token);
            try
            {
                if (_pending != pending) return;
                var accounts = State.Accounts.Where(a => a.AccountBinding != registration.AccountBinding).Append(registration).ToArray();
                Commit(State with { Accounts = accounts, Selected = registration.AccountBinding, PendingClientId = null });
                _pending = null;
                Publish(Describe(Authorized(registration) ? null : "ChatGPT identity was saved, but plan usage was not authorized. Continue with ChatGPT to enable it."));
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (pending.Token.IsCancellationRequested)
        {
            await _gate.WaitAsync();
            try
            {
                if (_pending == pending)
                { _pending = null; Publish(Describe("The ChatGPT login attempt expired. Start sign-in again.")); }
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex)
        {
            await _gate.WaitAsync();
            try
            {
                if (_pending == pending)
                {
                    _pending = null;
                    Publish(Describe(ex is ChatGptAuthException ? ex.Message : "ChatGPT sign-in could not be completed. Retry the authorization flow."));
                }
            }
            finally { _gate.Release(); }
        }
        finally { pending.Dispose(); }
    }

    public async Task<ChatGptCredential> GetCredentialAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var account = Selected;
            if (account is null || !Authorized(account)) throw new ChatGptAuthException("Continue with ChatGPT and authorize plan usage before starting a task.");
            if (account.ExpiresAt <= _time.GetUtcNow().AddMinutes(2))
            {
                if (account.RefreshToken is not { Length: > 0 }) throw new ChatGptAuthException("The ChatGPT session expired. Sign in again.");
                if (account.EarliestRefreshAt is { } earliest && earliest > _time.GetUtcNow())
                {
                    if (account.ExpiresAt <= _time.GetUtcNow()) throw new ChatGptAuthException("ChatGPT token renewal is not available yet. Try again shortly.");
                }
                else
                {
                    using var token = await PostAsync(_endpoints.Token, new()
                    {
                        ["grant_type"] = "refresh_token", ["client_id"] = account.ClientId,
                        ["refresh_token"] = account.RefreshToken, ["resource"] = Resource
                    }, cancellationToken);
                    var refreshed = await RegistrationAsync(token.RootElement, account.ClientId, account, null, cancellationToken);
                    // Only publish a newly rotated credential after the entire replacement has been saved.
                    Commit(State with { Accounts = State.Accounts.Select(a => a.AccountBinding == account.AccountBinding ? refreshed : a).ToArray() });
                    account = refreshed;
                    if (!Authorized(account))
                    {
                        Publish(Describe("ChatGPT plan usage is no longer authorized. Sign in again."));
                        throw new ChatGptAuthException("ChatGPT plan usage is no longer authorized. Sign in again.");
                    }
                }
            }
            return new(account.AccessToken!, account.AccountBinding, account.ExpiresAt);
        }
        catch (ChatGptAuthException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new ChatGptAuthException("ChatGPT token renewal failed. No request was sent with an unvalidated replacement."); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var credential = await GetCredentialAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoints.Models);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        using var document = await SendAsync(request, cancellationToken);
        if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new ChatGptAuthException("ChatGPT returned an invalid model catalog.");
        List<ModelInfo> result = [];
        foreach (var model in models.EnumerateArray().Take(256))
        {
            if (Text(model, "visibility") != "list" || Text(model, "slug") is not { Length: > 0 and <= 256 } id) continue;
            var name = Text(model, "display_name") ?? id;
            var efforts = model.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind == JsonValueKind.Array
                ? levels.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString() : Text(v, "effort")).Where(v => v is { Length: > 0 and <= 64 }).Cast<string>().Distinct(StringComparer.Ordinal).Take(16).ToArray() : [];
            bool? images = model.TryGetProperty("input_modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Array
                ? modalities.EnumerateArray().Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == "image") : null;
            if (result.All(m => m.Id != id)) result.Add(new(id, name, efforts, Text(model, "default_reasoning_level"), images));
        }
        await _gate.WaitAsync(cancellationToken);
        try { if (Selected?.AccountBinding != credential.AccountBinding) throw new ChatGptAuthException("The selected ChatGPT account changed while loading models. Refresh the catalog."); }
        finally { _gate.Release(); }
        return result;
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            CancelPending();
            var account = Selected;
            if (account is null) return;
            var remote = account.RefreshToken is null;
            if (account.RefreshToken is { } refresh)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                for (var attempt = 0; attempt < 2 && !deadline.IsCancellationRequested; attempt++)
                {
                    try
                    {
                        await LoadKeysAsync(false, deadline.Token);
                        if (_revocation is null) break;
                        using var request = new HttpRequestMessage(HttpMethod.Post, _revocation)
                        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refresh, ["token_type_hint"] = "refresh_token", ["client_id"] = account.ClientId }) };
                        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                        remote = response.StatusCode == HttpStatusCode.OK;
                        if (remote || (int)response.StatusCode < 500) break;
                    }
                    catch (ChatGptAuthException ex) when (ex.StatusCode is >= 400 and < 500) { break; }
                    catch (Exception) { /* Keep the refresh token until the bounded retry finishes. */ }
                    if (attempt == 0)
                    { try { await Task.Delay(TimeSpan.FromMilliseconds(250), deadline.Token); } catch (OperationCanceledException) { break; } }
                }
            }
            var cleared = account with { AccessToken = null, RefreshToken = null, IdToken = null, Scopes = [], ExpiresAt = DateTimeOffset.MinValue, EarliestRefreshAt = null };
            Commit(State with { Accounts = State.Accounts.Select(a => a.AccountBinding == account.AccountBinding ? cleared : a).ToArray() });
            Publish(Describe(remote ? null : "Signed out locally. Remote revocation was not confirmed; manage this app in ChatGPT Settings."));
        }
        finally { _gate.Release(); }
    }

    private async Task<AccountRegistration> RegistrationAsync(JsonElement token, string clientId, AccountRegistration? previous, string? nonce, CancellationToken ct)
    {
        var access = Required(token, "access_token", 64 * 1024);
        var refresh = nonce is null ? Required(token, "refresh_token", 64 * 1024) : Text(token, "refresh_token");
        if (refresh?.Length > 64 * 1024) throw new ChatGptAuthException("ChatGPT returned an oversized credential.");
        var identity = Required(token, "id_token", 64 * 1024);
        if (!string.Equals(Text(token, "token_type"), "Bearer", StringComparison.OrdinalIgnoreCase)) throw new ChatGptAuthException("The token service returned an unsupported credential type.");
        if (!token.TryGetProperty("expires_in", out var lifetime) || !lifetime.TryGetInt32(out var seconds) || seconds is < 1 or > 86400)
            throw new ChatGptAuthException("The token service returned an invalid credential lifetime.");
        var scopes = Required(token, "scope", 4096).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        var claims = await ValidateIdentityAsync(identity, clientId, nonce, ct);
        var subject = claims.Subject;
        if (previous is not null && (subject != previous.Subject || clientId != previous.ClientId))
            throw new ChatGptAuthException("The authorized account does not match the selected ChatGPT registration. The previous credentials were preserved.");
        DateTimeOffset? earliest = token.TryGetProperty("earliest_refresh_at", out var value) && value.TryGetInt64(out var timestamp)
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp) : null;
        return new(Binding(subject, clientId), subject, clientId, claims.Email ?? previous?.Email, access, refresh, identity,
            scopes, _time.GetUtcNow().AddSeconds(seconds), earliest);
    }

    private async Task<(string Subject, string? Email)> ValidateIdentityAsync(string encoded, string clientId, string? nonce, CancellationToken ct)
    {
        await LoadKeysAsync(false, ct);
        var handler = new JsonWebTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 64 * 1024 };
        async Task<TokenValidationResult> Validate() => await handler.ValidateTokenAsync(encoded, new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = _endpoints.Issuer.AbsoluteUri.TrimEnd('/'),
            ValidateAudience = true, ValidAudience = clientId, ValidateLifetime = true, RequireExpirationTime = true,
            RequireSignedTokens = true, ValidateIssuerSigningKey = true, IssuerSigningKeys = _keys!.GetSigningKeys(),
            ClockSkew = TimeSpan.FromSeconds(30), ValidAlgorithms = [SecurityAlgorithms.RsaSha256, SecurityAlgorithms.EcdsaSha256]
        });
        var result = await Validate();
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        { await LoadKeysAsync(true, ct); result = await Validate(); }
        if (!result.IsValid) throw new ChatGptAuthException("ChatGPT identity signature, issuer, audience or lifetime validation failed.");
        var subject = result.ClaimsIdentity.FindFirst("sub")?.Value;
        var authorizedParty = result.ClaimsIdentity.FindFirst("azp")?.Value;
        if (subject is not { Length: > 0 and <= 512 } ||
            (nonce is not null && !Equal(result.ClaimsIdentity.FindFirst("nonce")?.Value, nonce)) ||
            (authorizedParty is not null && authorizedParty != clientId) ||
            (result.SecurityToken is JsonWebToken jwt && jwt.Audiences.Skip(1).Any() && authorizedParty != clientId))
            throw new ChatGptAuthException("ChatGPT identity did not match the pending authorization.");
        var email = result.ClaimsIdentity.FindFirst("email")?.Value;
        return (subject, email is { Length: <= 512 } ? email : null);
    }

    private async Task LoadKeysAsync(bool force, CancellationToken ct)
    {
        if (!force && _keys is not null && _time.GetUtcNow() - _keysFetched < TimeSpan.FromHours(1)) return;
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoints.Discovery);
        using var discovery = await SendAsync(request, ct);
        var root = discovery.RootElement;
        if (Text(root, "issuer")?.TrimEnd('/') != _endpoints.Issuer.AbsoluteUri.TrimEnd('/')) throw new ChatGptAuthException("ChatGPT discovery returned an unexpected issuer.");
        var jwks = TrustedEndpoint(Required(root, "jwks_uri", 2048));
        if (Text(root, "revocation_endpoint") is { } revoke) _revocation = TrustedEndpoint(revoke);
        using var keyRequest = new HttpRequestMessage(HttpMethod.Get, jwks);
        using var keys = await SendAsync(keyRequest, ct);
        var parsed = new JsonWebKeySet(keys.RootElement.GetRawText());
        if (parsed.Keys.Count is < 1 or > 64) throw new ChatGptAuthException("ChatGPT discovery returned an invalid signing-key set.");
        _keys = parsed; _keysFetched = _time.GetUtcNow();
    }
    private Uri TrustedEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != _endpoints.Issuer.Scheme ||
            uri.Host != _endpoints.Issuer.Host || uri.Port != _endpoints.Issuer.Port || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ChatGptAuthException("ChatGPT discovery returned an unexpected endpoint.");
        return uri;
    }

    private Task<JsonDocument> PostAsync(Uri endpoint, Dictionary<string, string> fields, CancellationToken ct) =>
        SendOwnedAsync(new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(fields) }, ct);
    private async Task<JsonDocument> SendOwnedAsync(HttpRequestMessage request, CancellationToken ct)
    { using (request) return await SendAsync(request, ct); }
    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new ChatGptAuthException($"ChatGPT authorization service rejected the request (HTTP {(int)response.StatusCode}). Sign in again or try later.", (int)response.StatusCode);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[1024 * 1024 + 1];
            var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, ct);
            if (count > 1024 * 1024) throw new ChatGptAuthException("ChatGPT returned an oversized account response.");
            try { return JsonDocument.Parse(buffer.AsMemory(0, count).ToArray(), new JsonDocumentOptions { MaxDepth = 24 }); }
            finally { CryptographicOperations.ZeroMemory(buffer); }
        }
        catch (ChatGptAuthException) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new ChatGptAuthException("ChatGPT authorization service could not be reached or returned an invalid response."); }
    }
    private static string? Text(JsonElement root, string property) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Required(JsonElement root, string property, int limit) => Text(root, property) is { Length: > 0 } value && value.Length <= limit
        ? value : throw new ChatGptAuthException("ChatGPT returned an incomplete account response.");
    internal static string Binding(string subject, string client) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject + "\0" + client)));
    private static bool Equal(string? left, string right) => left is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));
    internal static string BuildUri(Uri uri, IReadOnlyDictionary<string, string> query) => uri.AbsoluteUri + "?" + string.Join('&', query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    private static async Task ReplyAsync(HttpListenerContext context, int status, string text, CancellationToken ct)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var data = Encoding.UTF8.GetBytes(text); context.Response.ContentLength64 = data.Length;
        await context.Response.OutputStream.WriteAsync(data, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        await _gate.WaitAsync();
        try { if (_disposed) return; _disposed = true; CancelPending(); }
        finally { _gate.Release(); }
        if (_loginTask is { } task) { try { await task; } catch (Exception) { } }
        _store.Dispose(); if (_ownsHttp) _http.Dispose(); _lifetime.Dispose();
    }

    private sealed class PendingLogin : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        internal string Id { get; } = Guid.NewGuid().ToString("N");
        internal string State { get; } = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        internal string Nonce { get; } = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        internal string Verifier { get; } = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(48));
        internal AccountRegistration? Previous { get; }
        internal string? ClientId { get; }
        internal HttpListener Listener { get; }
        internal Uri RedirectUri { get; }
        internal CancellationToken Token { get; }
        private int _disposed;
        internal PendingLogin(AccountRegistration? previous, string? unfinishedClient, CancellationToken lifetime)
        {
            Previous = previous;
            ClientId = previous?.ClientId ?? unfinishedClient;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _cancellation.CancelAfter(TimeSpan.FromMinutes(10)); Token = _cancellation.Token;
            for (var attempt = 0; ; attempt++)
            {
                using var port = new TcpListener(IPAddress.Loopback, 0);
                port.Start(); var number = ((IPEndPoint)port.LocalEndpoint).Port; port.Stop();
                var listener = new HttpListener(); listener.Prefixes.Add($"http://127.0.0.1:{number}/");
                try { listener.Start(); Listener = listener; RedirectUri = new($"http://127.0.0.1:{number}/auth/callback"); break; }
                catch (HttpListenerException) when (attempt < 8) { listener.Close(); }
            }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _cancellation.Cancel(); Listener.Close(); _cancellation.Dispose();
        }
    }
}
