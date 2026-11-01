using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Identity.Client.Extensions.Msal;
using Workbench.Protocol;

namespace Workbench.Auth;

/// <summary>Endpoint-bound API keys using the same native MSAL storage as account authentication, in a separate namespace.</summary>
public sealed class ApiCredentialStore : IDisposable
{
    private const string FileName = "api.credentials";
    private readonly string _directory;
    private readonly string _installation;
    private readonly FileStream _owner;
    private readonly TraceSource _quiet = new("Cogitide.ApiCredential.Storage", SourceLevels.Off);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Storage? _storage;
    private bool _disposed;

    public ApiCredentialStore(string directory)
    {
        _directory = Path.GetFullPath(Path.Combine(directory, "api-credentials"));
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _installation = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_directory)));
        _owner = new(Path.Combine(_directory, "credentials.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _quiet.Listeners.Clear();
    }
    private Storage Create(string name) => Storage.Create(new StorageCreationPropertiesBuilder(name, _directory)
        .WithMacKeyChain("Cogitide.ApiKeys", _installation)
        .WithLinuxKeyring("com.cogitide.apikeys", "default", "Cogitide API credentials",
            new("application", "cogitide-api"), new("installation", _installation)).Build(), _quiet);
    private Storage Storage => _storage ??= Create(FileName);

    public async Task<string> SaveAsync(ProviderSettings provider, string apiKey, CancellationToken ct = default)
    {
        ValidateKey(apiKey);
        var endpoint = ProviderCredentialBinding.Endpoint(provider.Endpoint);
        var family = ProviderCredentialBinding.Family(provider);
        if (provider.CredentialProfileId is { } profileId) ValidateProfileId(profileId);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var state = ReadState();
            var existing = state.SingleOrDefault(entry => entry.Id == provider.CredentialProfileId);
            string id;
            if (existing is not null)
            {
                if (existing.Endpoint != endpoint || existing.Provider != family)
                    throw new UnauthorizedAccessException("Stored API credential belongs to a different endpoint or provider protocol. Save a separate credential for this endpoint.");
                id = existing.Id;
                state[state.IndexOf(existing)] = existing with { ApiKey = apiKey };
            }
            else
            {
                if (state.Count >= 64) throw new InvalidOperationException("Protected API credential limit reached. Remove an unused credential before saving another.");
                id = Guid.NewGuid().ToString("N");
                state.Add(new(id, endpoint, family, apiKey));
            }
            ct.ThrowIfCancellationRequested();
            WriteState(state);
            return id;
        }
        finally { _gate.Release(); }
    }
    public async Task<string?> ReadAsync(ProviderSettings provider, CancellationToken ct = default)
    {
        var endpoint = ProviderCredentialBinding.Endpoint(provider.Endpoint);
        var family = ProviderCredentialBinding.Family(provider);
        if (string.IsNullOrWhiteSpace(provider.CredentialProfileId)) return null;
        ValidateProfileId(provider.CredentialProfileId);
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var found = ReadState().SingleOrDefault(entry => entry.Id == provider.CredentialProfileId);
            if (found is null) return null;
            if (found.Endpoint != endpoint || found.Provider != family)
                throw new UnauthorizedAccessException("Stored API credential belongs to a different endpoint or provider protocol. Save a separate credential for this endpoint.");
            return found.ApiKey;
        }
        finally { _gate.Release(); }
    }
    public async Task<ProviderCredentialStatus> StatusAsync(ProviderSettings provider, CancellationToken ct = default) =>
        new(await ReadAsync(provider, ct) is not null, provider.CredentialProfileId);
    public async Task DeleteAsync(ProviderSettings provider, CancellationToken ct = default)
    {
        // Validate the endpoint binding before deleting a credential named by arbitrary RPC input.
        if (await ReadAsync(provider, ct) is null) return;
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var state = ReadState();
            state.RemoveAll(entry => entry.Id == provider.CredentialProfileId);
            ct.ThrowIfCancellationRequested();
            WriteState(state);
        }
        finally { _gate.Release(); }
    }
    public static void ValidateKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length > 8192 || apiKey.Any(char.IsControl))
            throw new ArgumentException("API key must be nonempty and at most 8192 characters, without control characters.");
    }
    private static void ValidateProfileId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Invalid credential profile ID.");
    }
    private List<ApiCredentialEntry> ReadState()
    {
        try
        {
            var bytes = Storage.ReadData();
            try
            {
                if (bytes.Length == 0) return [];
                if (bytes.Length > 1024 * 1024) throw new InvalidDataException();
                return JsonSerializer.Deserialize(bytes, ApiCredentialJsonContext.Default.ListApiCredentialEntry) ?? throw new InvalidDataException();
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { throw new InvalidOperationException("Protected API credential storage is unavailable or invalid. Unlock the system vault; no plaintext fallback is enabled."); }
    }
    private void WriteState(List<ApiCredentialEntry> state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, ApiCredentialJsonContext.Default.ListApiCredentialEntry);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Create(FileName + ".new").WriteData(bytes);
                File.Move(Path.Combine(_directory, FileName + ".new"), Path.Combine(_directory, FileName), true);
            }
            else Storage.WriteData(bytes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { throw new InvalidOperationException("API key could not be saved to protected system storage. No plaintext fallback is enabled."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Dispose();
            _quiet.Close();
        }
        finally { _gate.Release(); }
    }
}

internal sealed record ApiCredentialEntry(string Id, string Endpoint, ProviderKind Provider, string ApiKey)
{
    public override string ToString() => nameof(ApiCredentialEntry);
}
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<ApiCredentialEntry>))]
internal partial class ApiCredentialJsonContext : JsonSerializerContext;
