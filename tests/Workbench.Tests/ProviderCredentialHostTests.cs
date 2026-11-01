using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Auth;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class ProviderCredentialHostTests
{
    [Fact]
    public async Task SavedGuiCredentialRunsThroughTheSharedKernelWithoutAnEnvironmentKey()
    {
        if (!OperatingSystem.IsWindows()) return; // Native vault acceptance on other OS requires an unlocked system vault.
        var root = Path.Combine(Path.GetTempPath(), "cogitide-provider-host-" + Guid.NewGuid().ToString("N"));
        var workspace = Directory.CreateDirectory(Path.Combine(root, "workspace")).FullName;
        var state = Directory.CreateDirectory(Path.Combine(root, "state")).FullName;
        var variable = "COGITIDE_UNSET_FIXTURE_" + Guid.NewGuid().ToString("N");
        var handler = new OpenAiResponsesTests.Handler((_, index) => OpenAiResponsesTests.Sse(OpenAiResponsesTests.TextResponse("Saved credential reached our kernel.", index)));
        using var http = new HttpClient(handler);
        try
        {
            await using var host = new WorkbenchHost(state, new NoAccounts(), http);
            var provider = new ProviderSettings("https://api.openai.com/v1", "fixture-responses", variable,
                Provider: ProviderKind.OpenAiResponses);
            var saved = (ProviderCredentialStatus)await host.DispatchAsync("provider.credential.save",
                Wire.Element(new { provider, apiKey = "native-vault-fixture-key" }));
            provider = provider with { CredentialProfileId = saved.CredentialProfileId };
            Assert.Null(Environment.GetEnvironmentVariable(variable));
            var session = (AgentSession)await host.DispatchAsync("session.create", Wire.Element(new { workspacePath = workspace, backend = "api" }));
            var receipt = Wire.Element(await host.DispatchAsync("run.start", Wire.Element(new
            {
                sessionId = session.Info.Id, prompt = "Reply without tools.", provider, options = new RunOptions(TimeoutSeconds: 30)
            })));
            Assert.True(receipt.GetProperty("accepted").GetBoolean());
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            IReadOnlyList<AgentEvent> events;
            while (true)
            {
                events = (IReadOnlyList<AgentEvent>)await host.DispatchAsync("session.events", Wire.Element(new { sessionId = session.Info.Id }));
                if (events.Any(e => e.Type is "run.completed" or "run.failed")) break;
                await Task.Delay(20, deadline.Token);
            }
            Assert.Contains(events, e => e.Type == "run.completed");
            Assert.Equal("native-vault-fixture-key", Assert.Single(handler.Credentials));
            Assert.DoesNotContain("native-vault-fixture-key", JsonSerializer.Serialize(events, Wire.Json));
            var loaded = (AgentSession)await host.DispatchAsync("session.load", Wire.Element(new { sessionId = session.Info.Id }));
            Assert.Contains(loaded.Messages, m => m.Role == "assistant" && m.Content == "Saved credential reached our kernel.");
            Assert.DoesNotContain("native-vault-fixture-key", JsonSerializer.Serialize(loaded, Wire.Json));
            Assert.Null(Environment.GetEnvironmentVariable(variable));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.DispatchAsync("provider.credential.status",
                Wire.Element(new { provider = provider with { Endpoint = "https://unselected.invalid/v1" } })));
            Assert.Single(handler.Requests);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private sealed class NoAccounts : IChatGptAccountService
    {
        public event Action<AccountState>? AccountChanged { add { } remove { } }
        public Task<AccountState> GetAccountAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AccountState(true, false));
        public Task<LoginInfo> LoginAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No account sign-in in API fixtures.");
        public Task CancelLoginAsync(string? loginId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LogoutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ChatGptCredential> GetCredentialAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("API runs must not ask for account credentials.");
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
