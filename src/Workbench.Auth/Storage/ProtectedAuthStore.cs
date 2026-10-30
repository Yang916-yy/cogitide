using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Workbench.Auth;

internal sealed record AccountRegistration(string AccountBinding, string Subject, string ClientId, string? Email,
    string? AccessToken, string? RefreshToken, string? IdToken, string[] Scopes, DateTimeOffset ExpiresAt,
    DateTimeOffset? EarliestRefreshAt = null);
internal sealed record AuthState(int Version, string HostId, string? Selected, IReadOnlyList<AccountRegistration> Accounts,
    string? PendingClientId = null);

internal interface IAuthStore : IDisposable
{
    AuthState? Read();
    void Write(AuthState state);
}

/// <summary>Microsoft's native DPAPI/Keychain/libsecret persistence; never opts into plaintext fallback.</summary>
internal sealed class ProtectedAuthStore : IAuthStore
{
    private const string FileName = "chatgpt.credentials";
    private readonly string _directory;
    private readonly string _installation;
    private readonly FileStream _owner;
    private readonly TraceSource _quiet = new("Cogito.Auth.Storage", SourceLevels.Off);
    private Storage? _storage;

    internal ProtectedAuthStore(string directory)
    {
        _directory = Path.GetFullPath(Path.Combine(directory, "oauth"));
        Directory.CreateDirectory(_directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _installation = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(_directory)));
        // The Host also owns its data directory. This narrower lock protects standalone consumers and rotating refresh tokens.
        _owner = new(Path.Combine(_directory, "auth.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        _quiet.Listeners.Clear();
    }

    private Storage Create(string name) => Storage.Create(new StorageCreationPropertiesBuilder(name, _directory)
        .WithMacKeyChain("Cogito.ChatGPT", _installation)
        .WithLinuxKeyring("com.cogito.chatgpt", "default", "Cogito ChatGPT account",
            new("application", "cogito"), new("installation", _installation)).Build(), _quiet);

    private Storage Storage => _storage ??= Create(FileName);

    public AuthState? Read()
    {
        try
        {
            var bytes = Storage.ReadData();
            try
            {
                if (bytes.Length == 0) return null;
                if (bytes.Length > 1024 * 1024) throw new InvalidDataException();
                return JsonSerializer.Deserialize(bytes, AuthJsonContext.Default.AuthState) ?? throw new InvalidDataException();
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception) { throw new ChatGptAuthException("Protected ChatGPT storage is unavailable or invalid. Unlock the system credential store; credentials were not read from plaintext files."); }
    }

    public void Write(AuthState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, AuthJsonContext.Default.AuthState);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // The helper encrypts using DPAPI. Atomic rename commits the complete rotating token set together.
                var temporary = FileName + ".new";
                Create(temporary).WriteData(bytes);
                File.Move(Path.Combine(_directory, temporary), Path.Combine(_directory, FileName), true);
            }
            else Storage.WriteData(bytes); // one native Keychain/Secret Service record update
        }
        catch (Exception) { throw new ChatGptAuthException("ChatGPT credentials could not be saved to protected system storage. No plaintext fallback is enabled."); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public void Dispose() { _owner.Dispose(); _quiet.Close(); }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AuthState))]
internal partial class AuthJsonContext : JsonSerializerContext;
