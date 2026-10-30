using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Auth;
using Workbench.Protocol;
using Workbench.Core;
using Xunit;

namespace Workbench.Tests;

public sealed class UnifiedAgentHostTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cogito-unified-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Combine(_directory, "workspace");
    private string State => Path.Combine(_directory, "state");
    public UnifiedAgentHostTests()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "src"));
        Directory.CreateDirectory(Path.Combine(Workspace, ".agents", "skills", "fixture-skill"));
        File.WriteAllText(Path.Combine(Workspace, "AGENTS.md"), "ROOT_RULE: Preserve existing files.");
        File.WriteAllText(Path.Combine(Workspace, "src", "AGENTS.md"), "SCOPED_RULE: Inspect the current file before making claims.");
        File.WriteAllText(Path.Combine(Workspace, "src", "task.txt"), "current observation");
        File.WriteAllText(Path.Combine(Workspace, ".agents", "skills", "fixture-skill", "SKILL.md"),
            "---\nname: fixture-skill\ndescription: Verify fixture sources before reuse.\n---\nRead the current file and keep evidence.");
    }

    [Fact]
    public async Task ChatGptUsesOwnCompactionRulesSkillsExperienceAndColdResume()
    {
        const string summary = "## Goal\nInspect current project.\n## Progress\nHistorical observations are preserved in the journal.\n## Next Steps\nRead current rules and verify current files.";
        var normal = 0;
        var summaries = 0;
        var resume = false;
        var handler = new OpenAiResponsesTests.Handler((body, index) =>
        {
            if (!body.TryGetProperty("tools", out var tools) || tools.GetArrayLength() == 0)
            {
                summaries++;
                Assert.Contains("context checkpoint", body.GetProperty("input")[0].GetRawText());
                return OpenAiResponsesTests.Sse(OpenAiResponsesTests.TextResponse(summary, index));
            }
            var input = body.GetProperty("input");
            Assert.Equal("developer", input[0].GetProperty("role").GetString());
            Assert.Contains("ROOT_RULE", input[0].GetRawText());
            Assert.Contains("Preserve original instructions.", input.GetRawText());
            var definitions = tools[0].GetProperty("tools").EnumerateArray().Select(tool => tool.GetProperty("name").GetString()).ToArray();
            Assert.Contains("read_skill", definitions);
            Assert.Contains("remember_experience", definitions);
            Assert.Contains("recall_project_memory", definitions);
            Assert.Contains("inspect_evidence", definitions);
            if (resume)
            {
                Assert.Contains("Historical context checkpoint", input.GetRawText());
                return OpenAiResponsesTests.Sse(OpenAiResponsesTests.TextResponse("Continued without replaying a tool.", index));
            }
            if (normal >= 2) Assert.Contains("SCOPED_RULE", input[0].GetRawText());
            var step = normal++;
            var call = step switch
            {
                0 => ("read_skill", "{\"name\":\"fixture-skill\"}"),
                1 => ("read_file", "{\"path\":\"src/task.txt\"}"),
                2 => ("inspect_evidence", "{}"),
                3 => ("remember_experience", Remember(input)),
                4 => ("recall_project_memory", "{\"query\":\"fixture observation\"}"),
                _ => ("", "")
            };
            if (step == 5)
            {
                var recalled = JsonDocument.Parse(LastOutput(input)).RootElement;
                Assert.Equal("Intact", recalled.GetProperty("matches")[0].GetProperty("sourceStatus").GetString());
            }
            return OpenAiResponsesTests.Sse(call.Item1.Length == 0 ? OpenAiResponsesTests.TextResponse("Finished with source evidence.", index)
                : OpenAiResponsesTests.ToolResponse(index, toolName: call.Item1, arguments: call.Item2));
        });
        using var http = new HttpClient(handler);
        var accounts = new Accounts();
        string id;
        AgentSession snapshot;
        await using (var host = new WorkbenchHost(State, accounts, http))
        {
            var initialization = Wire.Element(await host.DispatchAsync("initialize", Wire.Element(new { })));
            Assert.Equal("cogito", initialization.GetProperty("executionEngine").GetString());
            id = (await Create(host, "chatgpt")).Info.Id;
            List<ChatMessage> history = [new("user", "Preserve original instructions.")];
            for (var i = 0; i < 8; i++)
            {
                history.Add(new("assistant", "", [new("old_" + i, "read_file", "{\"path\":\"src/task.txt\"}")]));
                history.Add(new("tool", new string('x', 3500), ToolCallId: "old_" + i));
            }
            using (var store = new SqliteSessionStore(Path.Combine(State, "sessions.db")))
            {
                var session = (await store.LoadAsync(id))!;
                await store.SaveAsync(session with { Info = session.Info with { AccountBinding = Accounts.Binding }, Messages = history });
            }
            var run = await Start(host, id, maxContext: 20000);
            Assert.Equal("run.completed", (await Terminal(host, id, run)).Type);
            snapshot = (AgentSession)await host.DispatchAsync("session.load", Wire.Element(new { sessionId = id }));
            Assert.NotNull(snapshot.Compaction);
            Assert.True(summaries > 0);
            Assert.Equal(Accounts.Binding, snapshot.Info.AccountBinding);
            Assert.Equal(JsonSerializer.Serialize(history, Wire.Json), JsonSerializer.Serialize(snapshot.Messages.Take(history.Count), Wire.Json));
            var events = (IReadOnlyList<AgentEvent>)await host.DispatchAsync("session.events", Wire.Element(new { sessionId = id }));
            var completed = events.Where(evt => evt.Type == "tool.completed").ToArray();
            Assert.Equal(["read_skill", "read_file", "inspect_evidence", "remember_experience", "recall_project_memory"], completed.Select(evt => evt.Call!.Name));
            Assert.All(completed, evt => Assert.False(evt.Result!.IsError, evt.Result.Content));
            Assert.Contains(events, evt => evt.Type == "experience.saved");
            Assert.DoesNotContain("fixture-token", JsonSerializer.Serialize(snapshot, Wire.Json));
            Assert.All(handler.Uris, uri => Assert.Equal("https://api.openai.com/v1/responses", uri));
            Assert.All(handler.Credentials, credential => Assert.StartsWith("fixture-token-", credential));
        }
        resume = true;
        await using var restarted = new WorkbenchHost(State, new Accounts(), http);
        var restored = (AgentSession)await restarted.DispatchAsync("session.load", Wire.Element(new { sessionId = id }));
        Assert.Equal(JsonSerializer.Serialize(snapshot, Wire.Json), JsonSerializer.Serialize(restored, Wire.Json));
        var resumed = await Start(restarted, id, maxContext: 100000);
        Assert.Equal("run.completed", (await Terminal(restarted, id, resumed)).Type);
        var all = (IReadOnlyList<AgentEvent>)await restarted.DispatchAsync("session.events", Wire.Element(new { sessionId = id }));
        Assert.DoesNotContain(all, evt => evt.RunId == resumed && evt.Type == "tool.started");
    }

    [Fact]
    public async Task LegacyCodexHistoryCannotBeSilentlyReplayedAsOwnAgent()
    {
        await using var host = new WorkbenchHost(State, new Accounts());
        using var store = new SqliteSessionStore(Path.Combine(State, "sessions.db"));
        var session = new AgentSession(new("legacy", "Historical", Workspace, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            Backend: "codex", ExternalThreadId: "external-thread"), [new("user", "Historical request"), new("assistant", "Partial display history")]);
        await store.SaveAsync(session);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Start(host, "legacy"));
        Assert.Contains("historical Codex-managed", error.Message);
        Assert.Equal(JsonSerializer.Serialize(session, Wire.Json), JsonSerializer.Serialize(await store.LoadAsync("legacy"), Wire.Json));
        await Assert.ThrowsAsync<ArgumentException>(() => Create(host, "codex"));
    }

    [Fact]
    public async Task AccountSwitchBetweenModelStepsStopsBeforeAnyContextIsSentToNewAccount()
    {
        var account = new Accounts { SwitchOnCredentialCall = 3 };
        var handler = new OpenAiResponsesTests.Handler((_, index) => OpenAiResponsesTests.Sse(
            OpenAiResponsesTests.ToolResponse(index, toolName: "read_file", arguments: "{\"path\":\"src/task.txt\"}")));
        using var http = new HttpClient(handler);
        await using var host = new WorkbenchHost(State, account, http);
        var id = (await Create(host, "chatgpt")).Info.Id;
        Assert.Equal("run.failed", (await Terminal(host, id, await Start(host, id))).Type);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(handler.Credentials, credential => credential?.Contains("other", StringComparison.Ordinal) == true);
        account.SwitchOnCredentialCall = 1;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Start(host, id));
        Assert.Contains("another ChatGPT account", error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ChatGptReadOnlyUsesOurToolPolicyAndRejectsUnadvertisedCommands()
    {
        var handler = new OpenAiResponsesTests.Handler((body, index) =>
        {
            var names = body.GetProperty("tools")[0].GetProperty("tools").EnumerateArray().Select(item => item.GetProperty("name").GetString());
            Assert.DoesNotContain("run_command", names);
            if (index == 1) return OpenAiResponsesTests.Sse(OpenAiResponsesTests.ToolResponse(index,
                toolName: "run_command", arguments: "{\"command\":\"echo forbidden > forbidden.txt\"}"));
            Assert.Contains("Unknown tool", LastOutput(body.GetProperty("input")));
            return OpenAiResponsesTests.Sse(OpenAiResponsesTests.TextResponse("Read-only policy enforced.", index));
        });
        using var http = new HttpClient(handler);
        await using var host = new WorkbenchHost(State, new Accounts(), http);
        var id = (await Create(host, "chatgpt")).Info.Id;
        Assert.Equal("run.completed", (await Terminal(host, id, await Start(host, id, mode: ExecutionMode.ReadOnly))).Type);
        Assert.False(File.Exists(Path.Combine(Workspace, "forbidden.txt")));
        var events = (IReadOnlyList<AgentEvent>)await host.DispatchAsync("session.events", Wire.Element(new { sessionId = id }));
        Assert.DoesNotContain(events, evt => evt.Type is "tool.started" or "approval.requested");
    }

    [Theory]
    [InlineData("account.logout")]
    [InlineData("account.select")]
    public async Task AccountMutationCancelsRunBeforeSlowCredentialStoreOrRevocationCompletes(string method)
    {
        var account = new Accounts { BlockModelCredential = true, BlockMutation = true };
        var handler = new OpenAiResponsesTests.Handler((_, _) => throw new InvalidOperationException("Cancelled credentials must not reach the network."));
        using var http = new HttpClient(handler);
        await using var host = new WorkbenchHost(State, account, http);
        var id = (await Create(host, "chatgpt")).Info.Id;
        var run = await Start(host, id);
        await account.ModelCredentialStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var mutation = host.DispatchAsync(method, Wire.Element(new { accountBinding = "other-account" }));
        try
        {
            await account.MutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(mutation.IsCompleted);
            Assert.Equal("run.cancelled", (await Terminal(host, id, run)).Type);
            Assert.Empty(handler.Requests);
        }
        finally { account.MutationRelease.TrySetResult(); }
        await mutation;
    }

    [Fact]
    public async Task ResponsesApiKeyUsesTheSameEngineWithoutChatGptAuthorization()
    {
        var variable = "COGITO_FIXTURE_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "fixture-api-key");
        try
        {
            var handler = new OpenAiResponsesTests.Handler((body, index) =>
            {
                Assert.Equal("function", body.GetProperty("tools")[0].GetProperty("type").GetString());
                Assert.True(body.TryGetProperty("max_output_tokens", out _));
                return OpenAiResponsesTests.Sse(index == 1 ? OpenAiResponsesTests.ToolResponse(index, toolNamespace: null,
                    toolName: "read_file", arguments: "{\"path\":\"src/task.txt\"}") : OpenAiResponsesTests.TextResponse("Read current file.", index));
            });
            using var http = new HttpClient(handler);
            var account = new Accounts();
            await using var host = new WorkbenchHost(State, account, http);
            var id = (await Create(host, "api")).Info.Id;
            Assert.Equal("run.completed", (await Terminal(host, id, await Start(host, id, variable: variable))).Type);
            Assert.Equal(0, account.CredentialCalls);
            Assert.All(handler.Credentials, value => Assert.Equal("fixture-api-key", value));
            var events = (IReadOnlyList<AgentEvent>)await host.DispatchAsync("session.events", Wire.Element(new { sessionId = id }));
            Assert.Contains(events, evt => evt.Type == "tool.completed" && evt.Call!.Name == "read_file");
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    private async Task<AgentSession> Create(WorkbenchHost host, string backend) => (AgentSession)await host.DispatchAsync("session.create",
        Wire.Element(new { workspacePath = Workspace, backend }));
    private static async Task<string> Start(WorkbenchHost host, string id, int maxContext = 100000,
        ExecutionMode mode = ExecutionMode.WorkspaceWrite, string variable = "IGNORED")
    {
        var result = Wire.Element(await host.DispatchAsync("run.start", Wire.Element(new
        {
            sessionId = id, prompt = "Inspect the current project and retain original requirements.",
            provider = new ProviderSettings(OpenAiResponsesModel.OfficialEndpoint, "fixture-responses", variable, Provider: ProviderKind.OpenAiResponses),
            options = new RunOptions(MaxIterations: 12, TimeoutSeconds: 30, MaxContextCharacters: maxContext, ExecutionMode: mode)
        })));
        return result.GetProperty("runId").GetString()!;
    }
    private static async Task<AgentEvent> Terminal(WorkbenchHost host, string id, string run)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var events = (IReadOnlyList<AgentEvent>)await host.DispatchAsync("session.events", Wire.Element(new { sessionId = id }));
            var result = events.LastOrDefault(evt => evt.RunId == run && evt.Type is "run.completed" or "run.cancelled" or "run.failed");
            if (result is not null) return result;
            await Task.Delay(20, deadline.Token);
        }
    }
    private static string LastOutput(JsonElement input) => input.EnumerateArray().Last(item =>
        item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output").GetProperty("output").GetString()!;
    private static string Remember(JsonElement input)
    {
        var text = LastOutput(input);
        var evidence = JsonDocument.Parse(text[(text.IndexOf('\n') + 1)..]).RootElement;
        var source = evidence.GetProperty("entries").EnumerateArray().Last(item => item.GetProperty("toolName").GetString() == "read_file")
            .GetProperty("source").GetProperty("eventSequence").GetInt64();
        return JsonSerializer.Serialize(new { title = "fixture observation", preconditions = "Read the current fixture first.",
            procedure = "Inspect src/task.txt and report only the observed content.", pathPrefix = "src", evidenceSequences = new[] { source } });
    }
    private sealed class Accounts : IChatGptAccountService
    {
        internal const string Binding = "fixture-account-workspace";
        public int CredentialCalls { get; private set; }
        public int SwitchOnCredentialCall { get; set; } = int.MaxValue;
        public bool BlockModelCredential { get; init; }
        public bool BlockMutation { get; init; }
        public TaskCompletionSource ModelCredentialStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MutationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MutationRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<AccountState>? AccountChanged { add { } remove { } }
        public Task<AccountState> GetAccountAsync(CancellationToken ct = default) => Task.FromResult(new AccountState(true, true, AccountBinding: Binding));
        public Task<LoginInfo> LoginAsync(CancellationToken ct = default) => throw new InvalidOperationException("Tests must not open sign-in.");
        public Task<LoginInfo> LoginAsync(bool newAccount, CancellationToken ct = default) => LoginAsync(ct);
        public Task CancelLoginAsync(string? loginId = null, CancellationToken ct = default) => Task.CompletedTask;
        public async Task LogoutAsync(CancellationToken ct = default)
        {
            MutationStarted.TrySetResult();
            if (BlockMutation) await MutationRelease.Task.WaitAsync(ct);
        }
        public Task<IReadOnlyList<AccountProfile>> ListAccountsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AccountProfile>>([]);
        public async Task<AccountState> SelectAccountAsync(string accountBinding, CancellationToken ct = default)
        {
            await LogoutAsync(ct);
            return await GetAccountAsync(ct);
        }
        public async Task<ChatGptCredential> GetCredentialAsync(CancellationToken ct = default)
        {
            CredentialCalls++;
            if (BlockModelCredential && CredentialCalls > 1)
            {
                ModelCredentialStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            var binding = CredentialCalls >= SwitchOnCredentialCall ? "other-account" : Binding;
            return new ChatGptCredential("fixture-token-" + CredentialCalls, binding, DateTimeOffset.UtcNow.AddHours(1));
        }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>(
            [new("fixture-responses", "Fixture Responses", ["low", "high"], "low", true)]);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
