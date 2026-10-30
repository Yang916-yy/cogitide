using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Workbench.Auth;
using Workbench.Core;
using Workbench.Protocol;
using Workbench.Tools;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
if (args.FirstOrDefault() is "--optimize-skill" or "--optimize-compression")
{
    Environment.ExitCode = await ResearchCommandLine.RunAsync(args);
    return;
}
if (args is ["--command-worker"])
{
    try { await CommandSessionManager.RunWorkerAsync(); }
    catch (Exception ex)
    {
        var stage = ex.Data["CommandWorkerStage"] as string;
        if (stage is not ("read_launch" or "parse_launch" or "validate_workspace" or "persist_running" or "create_output" or
            "execute" or "flush_output" or "persist_terminal" or "dispose_output")) stage = "unknown";
        var recordStage = ex.Data["CommandRecordStage"] as string;
        if (recordStage is not ("write_snapshot" or "replace_snapshot" or "cleanup_snapshot")) recordStage = "none";
        await Console.Error.WriteLineAsync($"Command worker failed: {ex.GetType().Name} (0x{ex.HResult:X8}, stage={stage}, record={recordStage}).");
        Environment.ExitCode = 1;
    }
    return;
}
var dataIndex = Array.IndexOf(args, "--data-dir");
var dataDirectory = dataIndex >= 0 && dataIndex + 1 < args.Length
    ? Path.GetFullPath(args[dataIndex + 1])
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Workbench");
Directory.CreateDirectory(dataDirectory);
try
{
    using var owner = new FileStream(Path.Combine(dataDirectory, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    await using var host = new WorkbenchHost(dataDirectory);
    await host.RunAsync();
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"Workbench host could not start: {ex.Message}");
    Environment.ExitCode = 1;
}

internal sealed class WorkbenchHost : IAsyncDisposable
{
    private readonly SqliteSessionStore _store;
    private readonly AgentEngine _engine;
    private readonly WorkspaceService _workspace = new();
    private readonly CommandSessionManager _commands;
    private readonly IReadOnlyList<IAgentTool> _tools;
    private readonly IChatGptAccountService _accounts;
    private readonly HttpClient? _modelHttpClient;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _outputGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(16, 16);
    private readonly ConcurrentDictionary<string, ActiveRun> _runs = new();
    private readonly ConcurrentDictionary<string, string> _workspaceRuns = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _approvals = new();
    private readonly ConcurrentDictionary<long, Task> _requests = new();
    private long _requestNumber;

    internal WorkbenchHost(string directory, IChatGptAccountService? accounts = null, HttpClient? modelHttpClient = null)
    {
        _store = new SqliteSessionStore(Path.Combine(directory, "sessions.db"));
        _commands = new(Path.Combine(directory, "commands"), CommandWorkerStart);
        _tools = [.. WorkspaceTools.Create(_workspace, _commands), new BrowserCheckTool(Path.Combine(directory, "browser-artifacts"))];
        _engine = new AgentEngine(_store, _tools, WorkspaceService.Resolve, _commands,
            (workspace, revision, ct) => _workspace.CreateRepositoryMemoryToolAsync(workspace, revision, ct),
            async (workspace, excluded, ct) => File.Exists(Path.Combine(directory, "mcp.json"))
                ? await McpToolSet.ConnectAsync(Path.Combine(directory, "mcp.json"), workspace, excluded, ct) : null);
        _accounts = accounts ?? new ChatGptAccountService(directory);
        _modelHttpClient = modelHttpClient;
        _accounts.AccountChanged += AccountChanged;
    }

    internal static ProcessStartInfo CommandWorkerStart()
    {
        var executable = Environment.ProcessPath!;
        ProcessStartInfo start;
        if (Path.GetFileNameWithoutExtension(executable) == "Workbench.Host") start = new(executable);
        else
        {
            // This also supports the real Host assembly referenced by headless tests.
            var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
            var dotnet = Path.GetFullPath(Path.Combine(runtime, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            if (Path.GetFileNameWithoutExtension(executable) == "dotnet") dotnet = executable;
            start = new(File.Exists(dotnet) ? dotnet : "dotnet");
            start.ArgumentList.Add(typeof(WorkbenchHost).Assembly.Location);
        }
        start.ArgumentList.Add("--command-worker");
        return start;
    }

    private void AccountChanged(AccountState state)
    {
        foreach (var run in _runs.Values)
            if (run.Backend == "chatgpt" && (!state.LoggedIn || run.AccountBinding is { } binding && binding != state.AccountBinding))
                try { run.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        _ = NotifyAccountAsync(state);
    }

    private void CancelChatGptRuns()
    {
        foreach (var run in _runs.Values)
            if (run.Backend == "chatgpt")
                try { run.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task NotifyAccountAsync(AccountState state)
    {
        try { await WriteAsync(new RpcNotification("account.changed", Wire.Element(state))); }
        catch (Exception) { /* A detached UI cannot invalidate the account's durable state. */ }
    }

    public async Task RunAsync()
    {
        await _store.RecoverInterruptedAsync();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _shutdown.Cancel(); };
        try
        {
            while (await Console.In.ReadLineAsync(_shutdown.Token) is { } line)
            {
                if (line.Length > ImageInputs.MaxRequestCharacters)
                {
                    await WriteAsync(new RpcReply("invalid", Error: "Request exceeds the image and text size limit."));
                    continue;
                }
                // Approval, stop and steering must remain usable while slow RPCs occupy work slots.
                var control = IsControlRequest(line);
                if (!control && !await _requestGate.WaitAsync(0, _shutdown.Token))
                {
                    RpcRequest? rejected = null;
                    try { rejected = JsonSerializer.Deserialize<RpcRequest>(line, Wire.Json); } catch (JsonException) { }
                    await WriteAsync(new RpcReply(rejected?.Id ?? "invalid", Error: "Host is busy. Retry this request after pending operations finish."));
                    continue;
                }
                var number = Interlocked.Increment(ref _requestNumber);
                var task = HandleLineAsync(line);
                _requests[number] = task;
                _ = task.ContinueWith(_ => { _requests.TryRemove(number, out var ignored); if (!control) _requestGate.Release(); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            _shutdown.Cancel();
            foreach (var run in _runs.Values) run.Cancellation.Cancel();
            foreach (var approval in _approvals.Values) approval.TrySetCanceled();
            try { await Task.WhenAll(_runs.Values.Select(x => x.Task ?? Task.CompletedTask).Concat(_requests.Values)).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception e) when (e is OperationCanceledException or TimeoutException) { }
        }
    }

    private static bool IsControlRequest(string line)
    {
        try
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("method", out var method) &&
                method.ValueKind == JsonValueKind.String && method.GetString() is "run.cancel" or "run.steer" or "approval.respond" or "account.login.cancel" or "account.logout" or "account.select";
        }
        catch (JsonException) { return false; }
    }

    private async Task HandleLineAsync(string line)
    {
        RpcRequest? request = null;
        try
        {
            request = JsonSerializer.Deserialize<RpcRequest>(line, Wire.Json) ?? throw new JsonException("Empty request.");
            var result = await DispatchAsync(request.Method, request.Params);
            await WriteAsync(new RpcReply(request.Id, Wire.Element(result)));
        }
        catch (Exception e)
        {
            var message = e is OperationCanceledException ? "Operation cancelled." : e.Message;
            await WriteAsync(new RpcReply(request?.Id ?? "invalid", Error: message));
        }
    }

    internal async Task<object> DispatchAsync(string method, JsonElement p)
    {
        var ct = _shutdown.Token;
        switch (method)
        {
            case "initialize":
                return new { protocolVersion = Wire.Version, tools = _tools.Select(x => x.Definition).Concat([SessionEvidenceTool.Descriptor,
                    ProjectSkillTool.Descriptor, ProjectExperienceTools.RememberDescriptor, ProjectExperienceTools.RecallDescriptor]),
                    executionEngine = "cogito", chatGptAuthentication = "oauth", apiKeyAvailable = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")), defaultProvider = new ProviderSettings() };
            case "session.list": return await _store.ListAsync(ct);
            case "session.create":
            {
                var workspace = WorkspaceService.Root(Required(p, "workspacePath"));
                var now = DateTimeOffset.UtcNow;
                var backend = Optional(p, "backend") ?? "api";
                if (backend is not ("api" or "chatgpt")) throw new ArgumentException("Choose api or chatgpt authentication. Every new session uses the Cogitide agent engine.");
                var info = new SessionInfo(Guid.NewGuid().ToString("N"), Optional(p, "title") ?? Path.GetFileName(workspace), workspace, now, now, Backend: backend);
                var session = new AgentSession(info, []);
                await _store.SaveAsync(session, ct);
                return session;
            }
            case "session.load": return await LoadAsync(Required(p, "sessionId"), ct);
            case "session.evidence":
            {
                var session = await LoadAsync(Required(p, "sessionId"), ct);
                return await new SessionEvidenceTool(_store, session.Info.Id, session.Info.WorkspacePath,
                    _tools.Where(tool => tool.Definition.RequiresApproval).Select(tool => tool.Definition.Name).ToHashSet(StringComparer.Ordinal)).ReadSnapshotAsync(ct);
            }
            case "session.events": return await _store.EventsAsync(Required(p, "sessionId"), p.TryGetProperty("afterSequence", out var after) ? after.GetInt64() : 0, ct);
            case "project.rules":
                return await new ProjectRules(Required(p, "workspacePath"), WorkspaceService.Resolve).LoadAsync(Optional(p, "path") ?? "", cancellationToken: ct);
            case "project.skills":
                return await new ProjectRules(Required(p, "workspacePath"), WorkspaceService.Resolve).DiscoverSkillsAsync(ct);
            case "project.memory":
            {
                var session = await LoadAsync(Required(p, "sessionId"), ct);
                return await new ProjectExperienceTools(_store, session.Info.Id, "query", session.Info.WorkspacePath, WorkspaceService.Resolve)
                    .RecallAsync(Required(p, "query"), cancellationToken: ct);
            }
            case "run.start":
            {
                var id = Required(p, "sessionId");
                var session = await LoadAsync(id, ct);
                if (session.Info.Backend == "codex")
                    throw new InvalidOperationException("This is a historical Codex-managed chat with incomplete external tool history. Start a new ChatGPT chat to use the Cogitide agent; the original chat is preserved.");
                if (session.Info.Backend is not ("api" or "chatgpt")) throw new InvalidOperationException("Unknown session authentication mode.");
                var prompt = Required(p, "prompt");
                if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > 100000) throw new ArgumentException("Prompt must contain between 1 and 100000 characters.");
                var provider = p.TryGetProperty("provider", out var providerJson) ? providerJson.Deserialize<ProviderSettings>(Wire.Json) ?? new() : new();
                var images = p.TryGetProperty("images", out var imagesJson) ? imagesJson.Deserialize<ImageAttachment[]>(Wire.Json) : null;
                ImageInputs.Validate(images);
                if (session.Info.Backend == "api" && images is { Length: > 0 })
                    ModelCapabilities.ValidateImages([new ChatMessage("user", prompt, Images: images)], provider);
                if (session.Info.Backend == "chatgpt" && !p.TryGetProperty("provider", out _)) provider = provider with { Model = "" };
                var options = p.TryGetProperty("options", out var optionsJson) ? optionsJson.Deserialize<RunOptions>(Wire.Json) ?? new() : new();
                if (options.MaxIterations is < 1 or > 100 || options.TimeoutSeconds is < 1 or > 3600 || options.MaxContextCharacters is < 4000 or > 1000000)
                    throw new ArgumentException("Run limits are outside supported bounds.");
                if (!Enum.IsDefined(options.ExecutionMode)) throw new ArgumentException("Unknown execution mode.");
                var run = new ActiveRun(Guid.NewGuid().ToString("N"), CancellationTokenSource.CreateLinkedTokenSource(ct), session.Info.Backend);
                run.Cancellation.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                if (!_runs.TryAdd(id, run)) { run.Cancellation.Dispose(); throw new InvalidOperationException("This session is already running. Stop it before starting another turn."); }
                if (!_workspaceRuns.TryAdd(session.Info.WorkspacePath, id))
                {
                    _runs.TryRemove(id, out _); run.Cancellation.Dispose();
                    throw new InvalidOperationException("Another session is working in this workspace. Stop it or use an independent worktree.");
                }
                try
                {
                    if (session.Info.Backend == "api" && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(provider.ApiKeyEnvironmentVariable)))
                        throw new InvalidOperationException($"Set {provider.ApiKeyEnvironmentVariable} in your local environment, then restart Workbench.");
                    if (session.Info.Backend == "chatgpt")
                    {
                        var credential = await _accounts.GetCredentialAsync(run.Cancellation.Token);
                        if (session.Info.AccountBinding is { } binding && binding != credential.AccountBinding ||
                            session.Messages.Count > 0 && session.Info.AccountBinding is null)
                            throw new InvalidOperationException("This chat belongs to another ChatGPT account or has no verifiable account binding. Start a new chat after switching accounts.");
                        run.AccountBinding = credential.AccountBinding;
                        var models = await _accounts.ListModelsAsync(run.Cancellation.Token);
                        var selected = string.IsNullOrWhiteSpace(provider.Model) ? models.FirstOrDefault() : models.FirstOrDefault(model => model.Id == provider.Model);
                        if (selected is null) throw new InvalidOperationException("The selected ChatGPT model is not in this account's current catalog. Refresh the model list and choose an available model.");
                        if (images is { Length: > 0 } && selected.SupportsImages == false)
                            throw new ArgumentException("The selected ChatGPT model does not accept image input.");
                        if (provider.ReasoningEffort != "none" && (selected.ReasoningEfforts is not { } efforts || !efforts.Contains(provider.ReasoningEffort, StringComparer.Ordinal)))
                            throw new ArgumentException("The selected ChatGPT model does not advertise this reasoning effort.");
                        provider = provider with { Endpoint = OpenAiResponsesModel.OfficialEndpoint, Provider = ProviderKind.OpenAiResponses,
                            Model = selected.Id, ThinkingBudgetTokens = null, SupportsImages = selected.SupportsImages ?? true };
                        session = session with { Info = session.Info with { AccountBinding = credential.AccountBinding } };
                        await _store.SaveAsync(session, run.Cancellation.Token);
                    }
                    options = options with { RunId = run.Id };
                    run.Task = ExecuteRunAsync(session, prompt, provider, options, run, images);
                    return new { runId = run.Id, accepted = true };
                }
                catch { _runs.TryRemove(id, out _); _workspaceRuns.TryRemove(session.Info.WorkspacePath, out _); run.Cancellation.Dispose(); throw; }
            }
            case "run.cancel":
            {
                var id = Required(p, "sessionId");
                if (_runs.TryGetValue(id, out var run)) run.Cancellation.Cancel();
                return new { requested = true };
            }
            case "run.steer":
            {
                var id = Required(p, "sessionId");
                var message = Required(p, "message");
                if (string.IsNullOrWhiteSpace(message) || message.Length > 50000)
                    throw new ArgumentException("Additional instructions must contain between 1 and 50000 characters.");
                if (!_runs.ContainsKey(id)) throw new InvalidOperationException("This session has no active run. Send a new message instead.");
                await _engine.SteerAsync(id, message, ct);
                return new { accepted = true };
            }
            case "approval.respond":
            {
                var id = Required(p, "approvalId");
                if (!_approvals.TryGetValue(id, out var pending)) throw new InvalidOperationException("Approval is no longer pending.");
                return new { accepted = pending.TrySetResult(p.GetProperty("approved").GetBoolean()) };
            }
            case "workspace.files": return await _workspace.ListFilesAsync(Required(p, "workspacePath"), ct);
            case "workspace.read": return await _workspace.ReadFileAsync(Required(p, "workspacePath"), Required(p, "path"), ct);
            case "workspace.diff": return await _workspace.GitDiffAsync(Required(p, "workspacePath"), ct);
            case "workspace.command":
            {
                var workspace = WorkspaceService.Root(Required(p, "workspacePath"));
                if (!_workspaceRuns.TryAdd(workspace, "command:" + Guid.NewGuid().ToString("N")))
                    throw new InvalidOperationException("Stop the active workspace run before starting a separate command.");
                try { return await _workspace.RunCommandAsync(workspace, Required(p, "command"), p.TryGetProperty("timeoutSeconds", out var timeout) ? timeout.GetInt32() : 120, ct: ct); }
                finally { _workspaceRuns.TryRemove(workspace, out _); }
            }
            case "account.status": return await _accounts.GetAccountAsync(ct);
            case "account.list": return await _accounts.ListAccountsAsync(ct);
            case "account.select":
                CancelChatGptRuns();
                await _accounts.SelectAccountAsync(Required(p, "accountBinding"), ct);
                return await _accounts.GetAccountAsync(ct);
            case "account.login": return p.TryGetProperty("newAccount", out var newAccount) && newAccount.GetBoolean()
                ? await _accounts.LoginAsync(true, ct) : await _accounts.LoginAsync(ct);
            case "account.login.cancel": await _accounts.CancelLoginAsync(Optional(p, "loginId"), ct); return new { cancelled = true };
            case "account.logout":
                CancelChatGptRuns();
                await _accounts.LogoutAsync(ct);
                return new { loggedOut = true };
            case "model.list": return await _accounts.ListModelsAsync(ct);
            default: throw new ArgumentException($"Unknown method: {method}");
        }
    }

    private async Task ExecuteRunAsync(AgentSession session, string prompt, ProviderSettings provider, RunOptions options, ActiveRun run, IReadOnlyList<ImageAttachment>? images)
    {
        try
        {
            IAgentModel model;
            if (session.Info.Backend == "chatgpt")
                model = new OpenAiResponsesModel(provider, async token =>
                {
                    var credential = await _accounts.GetCredentialAsync(token);
                    if (credential.AccountBinding != session.Info.AccountBinding)
                        throw new InvalidOperationException("The ChatGPT account changed. Start a new chat; previous context was not sent to the new account.");
                    return credential.AccessToken;
                }, session.Info.AccountBinding!, _modelHttpClient);
            else
            {
                var key = Environment.GetEnvironmentVariable(provider.ApiKeyEnvironmentVariable)!;
                model = AgentModelFactory.Create(provider, key, _modelHttpClient);
            }
            options = options with { SecretEnvironmentVariables = [provider.ApiKeyEnvironmentVariable] };
            await _engine.RunAsync(session.Info.Id, prompt, model, options, EmitAsync, ApproveAsync, run.Cancellation.Token, images);
        }
        catch (Exception e)
        {
            if (run.TerminalEmitted) return;
            var cancelled = e is OperationCanceledException;
            var current = await _store.LoadAsync(session.Info.Id) ?? session;
            await _store.SaveAsync(current with { Info = current.Info with { Status = cancelled ? "cancelled" : "failed", UpdatedAt = DateTimeOffset.UtcNow } });
            var evt = await _store.AppendEventAsync(new(session.Info.Id, run.Id, cancelled ? "run.cancelled" : "run.failed", cancelled ? "Stopped by user." : e.Message));
            await EmitAsync(evt, CancellationToken.None);
        }
        finally
        {
            try { await _commands.EndRunAsync(session.Info.Id, run.Id); }
            finally
            {
                _runs.TryRemove(session.Info.Id, out _);
                _workspaceRuns.TryRemove(session.Info.WorkspacePath, out _);
                run.Cancellation.Dispose();
            }
        }
    }

    private async Task<bool> ApproveAsync(ApprovalRequest approval, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_approvals.TryAdd(approval.Id, completion)) throw new InvalidOperationException("Duplicate approval id.");
        using var registration = ct.Register(() => completion.TrySetCanceled(ct));
        try
        {
            // This notification occurs after registration, so an immediate UI response cannot race the dictionary insert.
            await WriteAsync(new RpcNotification("approval.requested", Wire.Element(approval)));
            return await completion.Task;
        }
        finally { _approvals.TryRemove(approval.Id, out _); }
    }

    private ValueTask EmitAsync(AgentEvent evt, CancellationToken ct)
    {
        if ((evt.Type is "run.completed" or "run.cancelled" or "run.failed") && _runs.TryGetValue(evt.SessionId, out var run)) run.TerminalEmitted = true;
        return new(WriteAsync(new RpcNotification("agent.event", Wire.Element(evt))));
    }
    private async Task WriteAsync<T>(T value)
    {
        await _outputGate.WaitAsync();
        try { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value, Wire.Json)); await Console.Out.FlushAsync(); }
        finally { _outputGate.Release(); }
    }
    private async Task<AgentSession> LoadAsync(string id, CancellationToken ct) => await _store.LoadAsync(id, ct) ?? throw new KeyNotFoundException("Session not found.");
    private static string Required(JsonElement p, string name) => Optional(p, name) ?? throw new ArgumentException($"Missing parameter: {name}");
    private static string? Optional(JsonElement p, string name) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _commands.DisposeAsync();
        _accounts.AccountChanged -= AccountChanged;
        await _accounts.DisposeAsync();
        try { await Task.WhenAll(_runs.Values.Select(run => run.Task ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        if (_store is IDisposable disposable) disposable.Dispose();
        _shutdown.Dispose();
    }
    private sealed class ActiveRun(string id, CancellationTokenSource cancellation, string backend)
    {
        public string Id { get; } = id;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public string Backend { get; } = backend;
        public string? AccountBinding { get; set; }
        public Task? Task { get; set; }
        public bool TerminalEmitted { get; set; }
    }
}
