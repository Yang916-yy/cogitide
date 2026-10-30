using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Workbench.Core;
using Workbench.Protocol;
using Workbench.Tools;

internal sealed record ResearchTaskPayload(string Repository, string BaseCommit, string Prompt,
    IReadOnlyList<string> ValidationCommands, IReadOnlyList<string> ProtectedPaths,
    IReadOnlyList<string>? AllowedCommands = null);
internal sealed record ResearchOptimizationSpec(ProviderSettings Provider, IReadOnlyList<SkillOptimizationTask> Tasks,
    string InitialSkill = "", SkillOptimizationOptions? Options = null, SkillModelPricing? Pricing = null,
    int TaskMaxIterations = 12, int TaskContextCharacters = 40000,
    CompressionGuidance? InitialGuidance = null, IReadOnlyList<CompressionFailureDiagnostic>? CompressionFailures = null,
    CompressionOptimizationBudget? CompressionBudget = null);

/// <summary>Explicit batch command; normal agent conversations never launch offline optimization.</summary>
internal static class ResearchCommandLine
{
    internal static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancel = (_, evt) => { evt.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        var token = cancellation.Token;
        string? artifactDirectory = null;
        try
        {
            if (args is not [var mode, var specification, "--output", var output, var action] ||
                mode is not ("--optimize-skill" or "--optimize-compression") || action is not ("--execute" or "--validate-only"))
                throw new ArgumentException("Usage: Workbench.Host --optimize-skill|--optimize-compression SPEC.json --output NEW_DIRECTORY --validate-only|--execute");
            var specPath = Path.GetFullPath(specification);
            if (new FileInfo(specPath).Length > 1024 * 1024) throw new InvalidDataException("Optimization specification exceeds 1 MiB.");
            var spec = JsonSerializer.Deserialize<ResearchOptimizationSpec>(await File.ReadAllTextAsync(specPath), Wire.Json)
                ?? throw new InvalidDataException("Missing specification.");
            Validate(spec, mode);
            var root = Path.GetFullPath(output);
            foreach (var task in spec.Tasks)
            {
                var payload = task.Payload.Deserialize<ResearchTaskPayload>(Wire.Json)!;
                var repository = Path.GetFullPath(payload.Repository);
                if (Path.GetRelativePath(repository, root) is var relative && !Path.IsPathRooted(relative) &&
                    relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
                    throw new ArgumentException("Optimization output must be outside all input repositories.");
            }
            if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                throw new IOException("Use a new empty output directory; runs are never overwritten.");
            if (action == "--validate-only")
            {
                Console.WriteLine(JsonSerializer.Serialize(new { valid = true, mode, tasks = spec.Tasks.Count, modelCalls = 0, executesCommands = false }, Wire.Json));
                return 0;
            }
            var key = Environment.GetEnvironmentVariable(spec.Provider.ApiKeyEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("The configured model key environment variable is not set.");
            Directory.CreateDirectory(root);
            artifactDirectory = root;
            await File.WriteAllTextAsync(Path.Combine(root, "batch-status.json"), "{\"status\":\"running\"}", token);
            var model = AgentModelFactory.Create(spec.Provider, key);
            var evaluator = new AgentTaskEvaluator(spec, Path.Combine(root, "evaluations"), () => AgentModelFactory.Create(spec.Provider, key));
            if (mode == "--optimize-skill")
            {
                var report = await new SkillOptimizer().OptimizeAsync(spec.InitialSkill, spec.Tasks,
                    new ModelSkillCandidateProposer(model, spec.Pricing), evaluator, spec.Options ?? new(), Path.Combine(root, "optimization"), token);
                token.ThrowIfCancellationRequested();
                await File.WriteAllTextAsync(Path.Combine(root, "batch-status.json"), JsonSerializer.Serialize(new { report.Status, report.HoldoutCompleted }, Wire.Json));
                Console.WriteLine(JsonSerializer.Serialize(new { report.Status, report.HoldoutCompleted, report.SelectedSkillSha256,
                    report.Usage, automaticallyActivated = false, reportDirectory = root }, Wire.Json));
                return report.Status == "completed" && report.HoldoutCompleted ? 0 : 1;
            }
            var baseline = spec.InitialGuidance ?? CompressionGuidance.Default;
            var budget = spec.CompressionBudget ?? new();
            cancellation.CancelAfter(TimeSpan.FromSeconds(budget.TimeoutSeconds));
            var proposal = await CompressionGuidanceOptimizer.ProposeAsync(baseline, spec.CompressionFailures!, model, budget, token);
            await proposal.Guidance.SaveAsync(Path.Combine(root, "candidate-guidance.json"), token);
            var selection = spec.Tasks.Where(task => task.Partition == SkillTaskPartition.Selection).ToDictionary(task => task.Id, StringComparer.Ordinal);
            var comparison = await CompressionGuidanceOptimizer.CompareAsync(baseline, proposal,
                selection.Keys.Select(id => new CompressionEvaluationCase(id, "selection")).ToArray(), async (guidance, test, remainingTokens, ct) =>
                {
                    var evaluation = await evaluator.EvaluateGuidanceAsync(guidance, selection[test.Id],
                        new(12, remainingTokens, remainingTokens, null, budget.TimeoutSeconds), ct);
                    if (evaluation.Result.Usage.InputTokens is not { } input || evaluation.Result.Usage.OutputTokens is not { } output)
                        throw new InvalidDataException("Model usage was not reported; compression optimization stopped.");
                    return new(test.Id, evaluation.Result.Score == 1, evaluation.Result.ConstraintsSatisfied,
                        evaluation.PeakContextCharacters, input, output);
                }, budget, token);
            await comparison.Selected.SaveAsync(Path.Combine(root, "selected-guidance.json"), token);
            await File.WriteAllTextAsync(Path.Combine(root, "comparison.json"), JsonSerializer.Serialize(comparison, Wire.Json));
            // Selection is frozen before final cases; held-out outcomes are reported and never fed back to the proposer.
            var usedTokens = comparison.ReportedTokens;
            var heldOut = new List<CompressionPairedScore>();
            foreach (var task in spec.Tasks.Where(task => task.Partition == SkillTaskPartition.Holdout))
            {
                var before = await FinalScoreAsync(baseline, task);
                var selected = comparison.Selected.Digest() == baseline.Digest() ? before : await FinalScoreAsync(comparison.Selected, task);
                heldOut.Add(new(task.Id, before, selected));
                await File.WriteAllTextAsync(Path.Combine(root, "holdout.json"), JsonSerializer.Serialize(new
                    { selectedGuidelineSha256 = comparison.Selected.Digest(), reportedTokens = usedTokens, cases = heldOut }, Wire.Json));
            }
            token.ThrowIfCancellationRequested();
            await File.WriteAllTextAsync(Path.Combine(root, "batch-status.json"), JsonSerializer.Serialize(new { status = "completed", comparison.Accepted, holdoutCompleted = true }, Wire.Json));
            Console.WriteLine(JsonSerializer.Serialize(new { comparison.Accepted, comparison.Reason, reportedTokens = usedTokens, holdoutCompleted = true,
                profile = Path.Combine(root, "selected-guidance.json"), automaticallyActivated = false }, Wire.Json));
            return 0;

            async Task<CompressionCaseScore> FinalScoreAsync(CompressionGuidance guideline, SkillOptimizationTask task)
            {
                var remaining = budget.MaxReportedTokens - usedTokens;
                if (remaining <= 0) throw new InvalidDataException("No token budget remains for final held-out evaluation.");
                var evaluation = await evaluator.EvaluateGuidanceAsync(guideline, task,
                    new(12, remaining, remaining, null, budget.TimeoutSeconds), token);
                token.ThrowIfCancellationRequested();
                if (evaluation.Result.Usage.InputTokens is not { } input || evaluation.Result.Usage.OutputTokens is not { } output)
                    throw new InvalidDataException("Held-out model usage is unavailable.");
                usedTokens = checked(usedTokens + input + output);
                if (usedTokens > budget.MaxReportedTokens) throw new InvalidDataException("An in-flight held-out call exceeded the remaining token budget.");
                return new(task.Id, evaluation.Result.Score == 1, evaluation.Result.ConstraintsSatisfied,
                    evaluation.PeakContextCharacters, input, output);
            }
        }
        catch (Exception ex)
        {
            if (artifactDirectory is not null)
                try { await File.WriteAllTextAsync(Path.Combine(artifactDirectory, "batch-status.json"), JsonSerializer.Serialize(new
                    { status = cancellation.IsCancellationRequested ? "cancelled" : "failed", errorType = ex.GetType().Name }, Wire.Json)); }
                catch (IOException) { }
            // Do not print provider exception bodies or credentials.
            await Console.Error.WriteLineAsync($"Research batch stopped ({ex.GetType().Name}): {SafeError(ex)}");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static string SafeError(Exception ex) => ex is ArgumentException or InvalidDataException or FileNotFoundException
        ? ex.Message : "Inspect local batch artifacts; no automatic retry was started.";

    internal static void Validate(ResearchOptimizationSpec spec, string mode)
    {
        if (mode is not ("--optimize-skill" or "--optimize-compression")) throw new ArgumentException("Unknown research optimization mode.");
        ArgumentNullException.ThrowIfNull(spec.Provider);
        _ = ModelCapabilities.ResolveProvider(spec.Provider);
        if (string.IsNullOrWhiteSpace(spec.Provider.Model) || spec.Provider.Model.Length > 256)
            throw new ArgumentException("Supply a nonempty model identifier of at most 256 characters.");
        if (spec.Options?.MaxCostUsd is not null && spec.Pricing is null)
            throw new ArgumentException("An explicit cost budget requires explicit model pricing.");
        if (spec.Pricing is { } pricing && (pricing.InputUsdPerMillionTokens < 0 || pricing.OutputUsdPerMillionTokens < 0))
            throw new ArgumentException("Model pricing cannot be negative.");
        if (string.IsNullOrWhiteSpace(spec.Provider.ApiKeyEnvironmentVariable) || spec.Provider.ApiKeyEnvironmentVariable.Length > 256 ||
            spec.Provider.ApiKeyEnvironmentVariable.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
            throw new ArgumentException("Use a valid key environment variable name.");
        if (!Uri.TryCreate(spec.Provider.Endpoint, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback))) throw new ArgumentException("Use HTTPS or a loopback fixture endpoint.");
        if (spec.Tasks is null || spec.Tasks.Count is < 3 or > 64 || spec.TaskMaxIterations is < 1 or > 30 ||
            spec.TaskContextCharacters is < 4000 or > 200000 || spec.Provider.MaxOutputTokens is < 1 or > 32768)
            throw new ArgumentException("Invalid task, iteration or context limits.");
        if (spec.Tasks.Select(task => task.Id).Distinct(StringComparer.Ordinal).Count() != spec.Tasks.Count ||
            spec.Tasks.Any(task => string.IsNullOrWhiteSpace(task.Id) || string.IsNullOrWhiteSpace(task.Family)) ||
            spec.Tasks.GroupBy(task => task.Family, StringComparer.Ordinal).Any(group => group.Select(task => task.Partition).Distinct().Count() > 1))
            throw new ArgumentException("Task IDs must be unique and problem families may not cross dataset partitions.");
        foreach (var task in spec.Tasks)
        {
            var payload = task.Payload.Deserialize<ResearchTaskPayload>(Wire.Json) ?? throw new InvalidDataException("Task payload is missing.");
            if (!Path.IsPathFullyQualified(payload.Repository) || !Directory.Exists(payload.Repository) ||
                payload.BaseCommit is null || payload.BaseCommit.Length is not (40 or 64) || !payload.BaseCommit.All(char.IsAsciiHexDigit) ||
                string.IsNullOrWhiteSpace(payload.Prompt) || payload.Prompt.Length > spec.TaskContextCharacters / 4 ||
                payload.ValidationCommands is not { Count: >= 1 and <= 8 } || payload.ValidationCommands.Any(string.IsNullOrWhiteSpace) ||
                payload.ProtectedPaths is not { Count: >= 1 and <= 32 } || payload.AllowedCommands?.Count > 16)
                throw new ArgumentException("Each task needs an absolute local repository, full frozen commit, prompt, validation commands and protected test paths.");
            foreach (var path in payload.ProtectedPaths) _ = WorkspaceService.Resolve(payload.Repository, path);
        }
        if (mode == "--optimize-skill" && string.IsNullOrWhiteSpace(spec.InitialSkill)) throw new ArgumentException("Supply an initial skill.");
        if (mode == "--optimize-skill") SkillOptimizer.ValidateConfiguration(spec.InitialSkill, spec.Tasks, spec.Options ?? new());
        if (mode == "--optimize-compression" && spec.CompressionFailures is not { Count: > 0 })
            throw new ArgumentException("Compression refinement needs development-only failure pairs.");
        if (mode == "--optimize-compression")
        {
            SkillOptimizer.ValidateConfiguration("Compression baseline", spec.Tasks, new());
            var developmentIds = spec.Tasks.Where(task => task.Partition == SkillTaskPartition.Train)
                .Select(task => task.Id).ToHashSet(StringComparer.Ordinal);
            if (spec.CompressionFailures!.Any(failure => !developmentIds.Contains(failure.CaseId)))
                throw new ArgumentException("Compression failure diagnostics must belong to declared training tasks; selection and held-out feedback cannot reach the proposer.");
            var budget = spec.CompressionBudget ?? new();
            if (budget.MaxEvaluations < 2 * spec.Tasks.Count(task => task.Partition is SkillTaskPartition.Selection or SkillTaskPartition.Holdout))
                throw new ArgumentException("Compression budget cannot cover paired selection and final holdout cases.");
            CompressionGuidanceOptimizer.ValidateConfiguration(spec.InitialGuidance ?? CompressionGuidance.Default,
                spec.CompressionFailures!, spec.Tasks.Where(task => task.Partition == SkillTaskPartition.Selection)
                    .Select(task => new CompressionEvaluationCase(task.Id, "selection")).ToArray(), budget);
        }
    }
}

internal sealed class AgentTaskEvaluator(ResearchOptimizationSpec spec, string evaluationRoot, Func<IAgentModel> modelFactory) : ISkillCandidateEvaluator
{
    internal sealed record RunResult(SkillEvaluation Result, long PeakContextCharacters);
    private readonly WorkspaceService _workspace = new();
    public async Task<SkillEvaluation> EvaluateAsync(SkillCandidate candidate, SkillOptimizationTask task, SkillOperationBudget budget,
        CancellationToken cancellationToken = default) => (await RunAsync(candidate.Text, null, task, budget, cancellationToken)).Result;
    internal Task<RunResult> EvaluateGuidanceAsync(CompressionGuidance guidance, SkillOptimizationTask task, SkillOperationBudget budget,
        CancellationToken cancellationToken) => RunAsync(null, guidance, task, budget, cancellationToken);

    private async Task<RunResult> RunAsync(string? skill, CompressionGuidance? guidance, SkillOptimizationTask task,
        SkillOperationBudget budget, CancellationToken ct)
    {
        var payload = task.Payload.Deserialize<ResearchTaskPayload>(Wire.Json)!;
        var folder = Path.Combine(evaluationRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var workspace = Path.Combine(folder, "workspace");
        var hooks = Path.Combine(folder, "disabled-hooks");
        Directory.CreateDirectory(hooks);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(budget.TimeoutSeconds));
        var token = timeout.Token;
        var meter = new MeteredModel(modelFactory(), budget, spec.Pricing, guidance is null ? null : Math.Min(budget.MaxInputTokens, budget.MaxOutputTokens));
        var feedback = new List<string>();
        var events = new List<AgentEvent>();
        try
        {
            Directory.CreateDirectory(workspace);
            await GitAsync(workspace, ["init", "--quiet"], hooks, token);
            // Fetch only the exact baseline tree, with no future refs/objects for the agent to inspect.
            await GitAsync(workspace, ["fetch", "--no-tags", "--depth=1", "--", payload.Repository, payload.BaseCommit], hooks, token);
            await GitAsync(workspace, ["checkout", "--detach", payload.BaseCommit, "--"], hooks, token);
            var protectedPaths = payload.ProtectedPaths.ToList();
            ResearchOptions? research = null;
            if (guidance is not null)
            {
                const string profilePath = ".cogitide-evaluation-guidance.json";
                if (File.Exists(Path.Combine(workspace, profilePath))) throw new IOException("Evaluation profile path collides with repository content.");
                await guidance.SaveAsync(Path.Combine(workspace, profilePath), token);
                protectedPaths.Add(profilePath);
                research = new(CompressionGuidance: true, CompressionGuidancePath: profilePath);
            }
            var protectedHashes = await SnapshotAsync(workspace, protectedPaths, token);
            using var store = new SqliteSessionStore(Path.Combine(folder, "session.db"));
            await using var commands = new CommandSessionManager(Path.Combine(folder, "commands"), WorkbenchHost.CommandWorkerStart);
            var now = DateTimeOffset.UtcNow;
            var session = new AgentSession(new(Guid.NewGuid().ToString("N"), task.Id, workspace, now, now), []);
            await store.SaveAsync(session, token);
            var prompt = payload.Prompt + (skill is null ? "" : "\n\nReusable procedure for this evaluation (reference only; current requirements and project rules take precedence):\n" + skill);
            var engine = new AgentEngine(store, WorkspaceTools.Create(_workspace, commands), WorkspaceService.Resolve, commands);
            await engine.RunAsync(session.Info.Id, prompt, meter,
                new(MaxIterations: spec.TaskMaxIterations, TimeoutSeconds: budget.TimeoutSeconds,
                    MaxContextCharacters: spec.TaskContextCharacters, Research: research, SecretEnvironmentVariables: [spec.Provider.ApiKeyEnvironmentVariable]),
                (evt, _) => { events.Add(evt); return ValueTask.CompletedTask; },
                async (approval, approvalToken) =>
                {
                    var beforeAction = await SnapshotAsync(workspace, protectedPaths, approvalToken);
                    return protectedHashes.SequenceEqual(beforeAction) && Approve(approval, payload, workspace, protectedPaths);
                }, token);
            var afterAgent = await SnapshotAsync(workspace, protectedPaths, token);
            var unchanged = protectedHashes.SequenceEqual(afterAgent);
            var passed = 0;
            foreach (var command in unchanged ? payload.ValidationCommands : [])
            {
                var beforeValidation = await SnapshotAsync(workspace, protectedPaths, token);
                if (!protectedHashes.SequenceEqual(beforeValidation)) { unchanged = false; break; }
                var result = await _workspace.RunCommandAsync(workspace, command, Math.Min(60, budget.TimeoutSeconds), ct: token,
                    excludedEnvironmentVariables: [spec.Provider.ApiKeyEnvironmentVariable]);
                if (!result.IsError) passed++;
                feedback.Add((result.IsError ? "FAIL: " : "PASS: ") + result.Content[..Math.Min(1800, result.Content.Length)]);
            }
            var afterValidation = await SnapshotAsync(workspace, protectedPaths, token);
            unchanged &= protectedHashes.SequenceEqual(afterValidation);
            var complete = events.LastOrDefault()?.Type == "run.completed";
            var score = unchanged && complete ? (double)passed / payload.ValidationCommands.Count : 0;
            feedback.Add($"Agent completed={complete}; frozen validation files unchanged={unchanged}.");
            return await SaveResultAsync(score, unchanged, null);
        }
        catch (OperationCanceledException)
        {
            feedback.Add("Evaluation cancelled or timed out; no success was inferred.");
            var result = await SaveResultAsync(0, false, "cancelled");
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception ex)
        {
            feedback.Add("Evaluation failed: " + ex.GetType().Name);
            return await SaveResultAsync(0, false, ex.GetType().Name);
        }

        async Task<RunResult> SaveResultAsync(double score, bool constraints, string? error)
        {
            var trace = JsonSerializer.Serialize(events, Wire.Json);
            await File.WriteAllTextAsync(Path.Combine(folder, "events.json"), trace);
            var result = new SkillEvaluation(task.Id, score, constraints, string.Join("\n", feedback), meter.Usage,
                Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(trace))), error);
            await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(result, Wire.Json));
            return new(result, meter.PeakContextCharacters);
        }
    }

    private static bool Approve(ApprovalRequest request, ResearchTaskPayload task, string workspace, IReadOnlyList<string> protectedPaths)
    {
        using var arguments = JsonDocument.Parse(request.ArgumentsJson);
        if (request.ToolName is "write_file" or "replace_text" or "apply_patch")
        {
            var paths = request.ToolName == "apply_patch"
                ? WorkspaceService.DescribePatchPaths(workspace, arguments.RootElement.GetProperty("patchText").GetString()!)
                : [arguments.RootElement.GetProperty("path").GetString()!];
            var protectedFullPaths = protectedPaths.Select(path => WorkspaceService.Resolve(workspace, path)).ToHashSet(
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            return paths.All(path => !protectedFullPaths.Contains(WorkspaceService.Resolve(workspace, path, true)));
        }
        if (request.ToolName is not ("run_command" or "start_command")) return false;
        return arguments.RootElement.TryGetProperty("command", out var command) &&
            task.AllowedCommands?.Contains(command.GetString(), StringComparer.Ordinal) == true;
    }
    private async Task GitAsync(string directory, string[] arguments, string hooks, CancellationToken ct)
    {
        var result = await _workspace.RunProgramAsync(directory, "git",
            ["-c", "core.hooksPath=" + hooks, "-c", "core.fsmonitor=false", "-c", "protocol.file.allow=always", .. arguments], 60, ct,
            [spec.Provider.ApiKeyEnvironmentVariable]);
        if (result.IsError) throw new IOException("Frozen repository checkout failed.");
    }
    private static async Task<KeyValuePair<string, string>[]> SnapshotAsync(string workspace, IReadOnlyList<string> paths, CancellationToken ct)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var path in paths.Order(StringComparer.Ordinal))
        {
            await using var input = File.OpenRead(WorkspaceService.Resolve(workspace, path));
            if (input.Length > 16 * 1024 * 1024) throw new InvalidDataException("Protected validation file exceeds 16 MiB.");
            result.Add(new(path, await EvidenceFileHash.ComputeAsync(input, cancellationToken: ct)
                ?? throw new InvalidDataException("Protected file grew beyond its byte budget.")));
        }
        return result.ToArray();
    }
}

internal sealed class MeteredModel(IAgentModel inner, SkillOperationBudget budget, SkillModelPricing? pricing,
    long? combinedTokenLimit = null) : IAgentModel
{
    private long _input, _output;
    private int _calls;
    private bool _known = true;
    internal long PeakContextCharacters { get; private set; }
    internal SkillUsage Usage => new(_known ? _input : null, _known ? _output : null, _calls,
        _calls == 0 ? 0 : pricing is null || !_known ? null : (_input * pricing.InputUsdPerMillionTokens + _output * pricing.OutputUsdPerMillionTokens) / 1_000_000m);
    public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_known || _calls >= budget.MaxModelCalls || _input >= budget.MaxInputTokens || _output >= budget.MaxOutputTokens ||
            (combinedTokenLimit is { } combined && _input + _output >= combined) ||
            (budget.MaxCostUsd is { } max && (Usage.CostUsd is null || Usage.CostUsd >= max)))
            throw new InvalidOperationException("Evaluation model budget exhausted or accounting unavailable.");
        _calls++;
        PeakContextCharacters = Math.Max(PeakContextCharacters, JsonSerializer.Serialize(messages, Wire.Json).Length);
        long? input = null, output = null;
        try
        {
            await foreach (var update in inner.StreamAsync(messages, tools, cancellationToken))
            {
                input = update.InputTokens ?? input;
                output = update.OutputTokens ?? output;
                yield return update;
            }
        }
        finally
        {
            _known &= input is not null && output is not null;
            _input += input ?? 0;
            _output += output ?? 0;
        }
    }
}
