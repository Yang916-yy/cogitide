using Workbench.Protocol;

namespace Workbench.Auth;

public sealed record ChatGptCredential(string AccessToken, string AccountBinding, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"ChatGPT credential ({AccountBinding}, expires {ExpiresAt:O})";
}

/// <summary>Owns account authorization only. Model/tool execution belongs to the application's AgentEngine.</summary>
public interface IChatGptAccountService : IAsyncDisposable
{
    event Action<AccountState>? AccountChanged;
    Task<AccountState> GetAccountAsync(CancellationToken cancellationToken = default);
    Task<LoginInfo> LoginAsync(CancellationToken cancellationToken = default);
    Task CancelLoginAsync(string? loginId, CancellationToken cancellationToken = default);
    Task LogoutAsync(CancellationToken cancellationToken = default);
    Task<ChatGptCredential> GetCredentialAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AccountProfile>> ListAccountsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AccountProfile>>([]);
    Task<AccountState> SelectAccountAsync(string accountBinding, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Account selection is not available.");
    Task<LoginInfo> LoginAsync(bool newAccount, CancellationToken cancellationToken = default) => LoginAsync(cancellationToken);
}

/// <summary>Only safe, fixed messages cross the RPC boundary; upstream bodies and credentials are never retained.</summary>
public sealed class ChatGptAuthException(string message, int? statusCode = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
}
