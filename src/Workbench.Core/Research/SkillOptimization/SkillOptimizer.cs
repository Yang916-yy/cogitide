using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Workbench.Core;

/// <summary>
/// Explicit offline text optimization. GEPA explores instance-wise Pareto winners; SkillOpt keeps a
/// validation-gated incumbent. Both share evaluation, accounting and immutable artifact machinery.
/// No ordinary conversation starts this loop and no candidate is installed by this component.
/// </summary>
public sealed class SkillOptimizer
{
    public async Task<SkillOptimizationReport> OptimizeAsync(string initialSkill,
        IReadOnlyList<SkillOptimizationTask> tasks, ISkillCandidateProposer proposer,
        ISkillCandidateEvaluator evaluator, SkillOptimizationOptions options, string artifactDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposer);
        ArgumentNullException.ThrowIfNull(evaluator);
        ValidateConfiguration(initialSkill, tasks, options);
        options = options with { RequiredLiterals = options.RequiredLiterals?.ToArray() ?? [] };
        var frozenTasks = tasks.OrderBy(task => task.Id, StringComparer.Ordinal)
            .Select(task => task with { Payload = task.Payload.Clone() }).ToArray();
        var train = frozenTasks.Where(task => task.Partition == SkillTaskPartition.Train).ToArray();
        var selection = frozenTasks.Where(task => task.Partition == SkillTaskPartition.Selection).ToArray();
        var holdout = frozenTasks.Where(task => task.Partition == SkillTaskPartition.Holdout).ToArray();
        var development = train.Concat(selection).ToArray();
        var manifest = JsonSerializer.Serialize(frozenTasks, SkillOptimizationJsonContext.Default.SkillOptimizationTaskArray);
        var baseline = Candidate(initialSkill, [], 0);
        List<SkillCandidate> candidates = [baseline];
        List<SkillCandidateEvaluation> evaluations = [];
        List<SkillOptimizationDecision> decisions = [];
        List<SkillCandidate> frontier = [baseline];
        var selected = baseline;
        var runId = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.UtcNow;
        var status = "running";
        string? error = null;
        var holdoutCompleted = false;
        var usage = new Accounting(options);
        var random = new Random(options.Seed);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        var token = deadline.Token;
        await using var artifacts = new OptimizationArtifacts(artifactDirectory);
        await artifacts.CreateManifestAsync(manifest, baseline);

        SkillOptimizationReport Report() => new(1, runId, options.Strategy, status, Hash(manifest), baseline.Sha256,
            selected.Id, selected.Sha256, candidates.ToArray(), evaluations.ToArray(), decisions.ToArray(),
            frontier.Select(candidate => candidate.Id).ToArray(), usage.Snapshot(), options, started,
            DateTimeOffset.UtcNow, holdoutCompleted, error);
        async Task SaveAsync() => await artifacts.SaveReportAsync(Report());
        async Task EvaluateAsync(SkillCandidate candidate, IReadOnlyList<SkillOptimizationTask> partition)
        {
            foreach (var task in partition)
            {
                token.ThrowIfCancellationRequested();
                var remaining = usage.BeginOperation(proposal: false);
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
                operation.CancelAfter(TimeSpan.FromSeconds(remaining.TimeoutSeconds));
                SkillEvaluation result;
                try { result = await evaluator.EvaluateAsync(candidate, task, remaining, operation.Token); }
                catch (OperationCanceledException) { usage.MarkUnknownOperation(); throw; }
                catch (Exception ex)
                {
                    usage.MarkUnknownOperation();
                    throw new OptimizationStop("evaluation_failed", "Evaluator failed: " + ex.GetType().Name + ". Usage and outcome of the interrupted operation are unknown.");
                }
                try { ValidateEvaluation(task, result); }
                catch { usage.MarkUnknownOperation(); throw; }
                // Persist even an over-budget operation, rather than hiding a consumed request.
                evaluations.Add(new(candidate.Id, task.Partition, result with { Feedback = Prefix(result.Feedback, 4000) }));
                try { usage.Add(result.Usage); }
                finally { await SaveAsync(); }
                // An evaluator may return a known result after cancellation instead of throwing. Retain
                // its observed usage/diagnostics, but do not let that result advance selection or completion.
                operation.Token.ThrowIfCancellationRequested();
            }
        }
        try
        {
            await SaveAsync();
            await EvaluateAsync(baseline, development);
            for (var round = 1; round <= options.MaxProposals && candidates.Count < options.MaxCandidates; round++)
            {
                token.ThrowIfCancellationRequested();
                // Preserve invocation slots for both fixed final comparisons; never borrow held-out data for search.
                if (options.MaxEvaluations - usage.Evaluations < development.Length + 2 * holdout.Length) break;
                var parent = options.Strategy == SkillOptimizationStrategy.SkillOpt ? selected
                    : SamplePareto(frontier, selection, evaluations, random);
                List<SkillCandidate> parents = [parent];
                if (options.Strategy == SkillOptimizationStrategy.Gepa && options.EnableMerge && round % 3 == 0 && frontier.Count > 1)
                    parents.Add(frontier.Where(item => item.Id != parent.Id).OrderBy(item => item.Id, StringComparer.Ordinal).First());
                // Only training cases and training diagnostics reach the reflective model.
                var observations = parents.SelectMany(item => train.Select(task =>
                {
                    var evaluation = Find(evaluations, item.Id, task.Id);
                    return new SkillTrainingObservation(task.Id, task.Family, task.Payload,
                        evaluation.Score, evaluation.ConstraintsSatisfied, Prefix(evaluation.Feedback, 2000));
                })).OrderBy(item => item.ConstraintsSatisfied).ThenBy(item => item.Score).Take(8).ToArray();
                var proposalRequest = new SkillProposalRequest(options.Strategy, round, parents.ToArray(), observations,
                    options.MaxCandidateCharacters, options.MaxChangedCharacters, options.RequiredLiterals!);
                var remaining = usage.BeginOperation(proposal: true);
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
                operation.CancelAfter(TimeSpan.FromSeconds(remaining.TimeoutSeconds));
                SkillProposal proposal;
                try { proposal = await proposer.ProposeAsync(proposalRequest, remaining, operation.Token); }
                catch (OperationCanceledException) { usage.MarkUnknownOperation(); throw; }
                catch (Exception ex)
                {
                    usage.MarkUnknownOperation();
                    throw new OptimizationStop("proposal_failed", "Proposer failed: " + ex.GetType().Name + ". Usage of the interrupted operation is unknown.");
                }
                var parentIds = parents.Select(item => item.Id).ToArray();
                var reflection = Prefix(proposal.Reflection ?? "", 4000);
                var invalid = ValidateProposal(proposal, parent, options);
                var candidate = invalid is null ? Candidate(proposal.Text, parentIds, round) : null;
                if (candidate is not null && candidates.Any(item => item.Sha256 == candidate.Sha256)) invalid = "duplicate_skill";
                try { usage.Add(proposal.Usage); }
                catch
                {
                    decisions.Add(new(round, parentIds, candidate?.Id, "not_evaluated", "accounting_limit", reflection));
                    throw;
                }
                operation.Token.ThrowIfCancellationRequested();
                if (invalid is not null)
                {
                    decisions.Add(new(round, parentIds, candidate?.Id, "rejected", invalid, reflection));
                    await SaveAsync();
                    continue;
                }
                candidates.Add(candidate!);
                await artifacts.SaveCandidateAsync(candidate!);
                await SaveAsync();
                await EvaluateAsync(candidate!, train);
                // GEPA's minibatch improvement check avoids spending selection evaluations on an unimproved mutation.
                if (options.Strategy == SkillOptimizationStrategy.Gepa &&
                    Mean(candidate!, train, evaluations) <= parents.Max(item => Mean(item, train, evaluations)) + 1e-9)
                {
                    decisions.Add(new(round, parentIds, candidate!.Id, "rejected", "no_training_improvement", reflection));
                    await SaveAsync();
                    continue;
                }
                await EvaluateAsync(candidate!, selection);
                if (options.Strategy == SkillOptimizationStrategy.SkillOpt)
                {
                    var noRegression = selection.All(task => Effective(Find(evaluations, candidate!.Id, task.Id)) + 1e-9 >=
                        Effective(Find(evaluations, selected.Id, task.Id)));
                    var improved = Mean(candidate!, selection, evaluations) > Mean(selected, selection, evaluations) + 1e-9;
                    if (noRegression && improved)
                    {
                        selected = candidate!;
                        frontier = [selected];
                        decisions.Add(new(round, parentIds, candidate!.Id, "accepted", "validation_improved_without_task_regression", reflection));
                    }
                    else decisions.Add(new(round, parentIds, candidate!.Id, "rejected",
                        noRegression ? "no_validation_improvement" : "validation_regression", reflection));
                }
                else
                {
                    var eligible = candidates.Where(item => selection.All(task => Has(evaluations, item.Id, task.Id))).ToArray();
                    frontier = ParetoWinners(eligible, selection, evaluations);
                    selected = Best(eligible, selection, evaluations);
                    decisions.Add(new(round, parentIds, candidate!.Id, frontier.Any(item => item.Id == candidate!.Id) ? "frontier" : "evaluated",
                        "instance_wise_pareto_selection", reflection));
                }
                await SaveAsync();
            }
            // Selection is frozen before either held-out run. No outcome below can select a different skill.
            token.ThrowIfCancellationRequested();
            decisions.Add(new(usage.Proposals, [], selected.Id, "selection_frozen", "holdout_is_report_only", ""));
            await SaveAsync();
            await EvaluateAsync(baseline, holdout);
            if (selected.Id != baseline.Id) await EvaluateAsync(selected, holdout);
            token.ThrowIfCancellationRequested();
            holdoutCompleted = true;
            status = "completed";
        }
        catch (OptimizationStop stop) { status = stop.Status; error = stop.Message; }
        catch (OperationCanceledException)
        {
            status = cancellationToken.IsCancellationRequested ? "cancelled" : "time_limit";
            error = "The offline run stopped. In-flight evaluation may be incomplete; no candidate was activated.";
        }
        catch (Exception ex)
        {
            status = "failed";
            error = "Offline optimization failed: " + ex.GetType().Name + ". Inspect retained artifacts; no candidate was activated.";
        }
        await SaveAsync();
        await artifacts.SaveSelectedAsync(selected);
        return Report();
    }

    private static SkillCandidate Candidate(string text, IReadOnlyList<string> parentIds, int generation)
    {
        var digest = Hash(text);
        return new("skill-" + digest[..16], text, digest, parentIds.ToArray(), generation);
    }
    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string Prefix(string text, int limit)
    {
        if (text.Length <= limit) return text;
        if (limit > 0 && char.IsHighSurrogate(text[limit - 1])) limit--;
        return text[..limit];
    }
    private static bool Has(IEnumerable<SkillCandidateEvaluation> rows, string candidate, string task) =>
        rows.Any(row => row.CandidateId == candidate && row.Result.TaskId == task);
    private static SkillEvaluation Find(IEnumerable<SkillCandidateEvaluation> rows, string candidate, string task) =>
        rows.Single(row => row.CandidateId == candidate && row.Result.TaskId == task).Result;
    private static double Effective(SkillEvaluation evaluation) => evaluation.ConstraintsSatisfied && evaluation.Error is null ? evaluation.Score : -1;
    private static double Mean(SkillCandidate candidate, IReadOnlyList<SkillOptimizationTask> tasks,
        IReadOnlyList<SkillCandidateEvaluation> rows) => tasks.Average(task => Effective(Find(rows, candidate.Id, task.Id)));
    private static SkillCandidate Best(IEnumerable<SkillCandidate> candidates, IReadOnlyList<SkillOptimizationTask> tasks,
        IReadOnlyList<SkillCandidateEvaluation> rows) => candidates.OrderByDescending(item => Mean(item, tasks, rows))
            .ThenBy(item => item.Generation).ThenBy(item => item.Id, StringComparer.Ordinal).First();

    private static List<SkillCandidate> ParetoWinners(IReadOnlyList<SkillCandidate> candidates,
        IReadOnlyList<SkillOptimizationTask> tasks, IReadOnlyList<SkillCandidateEvaluation> rows)
    {
        var maxima = tasks.ToDictionary(task => task.Id, task => candidates.Max(item => Effective(Find(rows, item.Id, task.Id))));
        var winners = candidates.Where(item => tasks.Any(task => Effective(Find(rows, item.Id, task.Id)) >= maxima[task.Id] - 1e-9)).ToArray();
        return winners.Where(item => !winners.Any(other => other.Id != item.Id &&
            tasks.All(task => Effective(Find(rows, other.Id, task.Id)) >= Effective(Find(rows, item.Id, task.Id)) - 1e-9) &&
            tasks.Any(task => Effective(Find(rows, other.Id, task.Id)) > Effective(Find(rows, item.Id, task.Id)) + 1e-9)))
            .OrderBy(item => item.Id, StringComparer.Ordinal).ToList();
    }
    private static SkillCandidate SamplePareto(IReadOnlyList<SkillCandidate> frontier,
        IReadOnlyList<SkillOptimizationTask> tasks, IReadOnlyList<SkillCandidateEvaluation> rows, Random random)
    {
        var maxima = tasks.ToDictionary(task => task.Id, task => frontier.Max(item => Effective(Find(rows, item.Id, task.Id))));
        var tickets = frontier.SelectMany(item => Enumerable.Repeat(item,
            tasks.Count(task => Effective(Find(rows, item.Id, task.Id)) >= maxima[task.Id] - 1e-9))).ToArray();
        return tickets[random.Next(tickets.Length)];
    }
    private static string? ValidateProposal(SkillProposal proposal, SkillCandidate parent, SkillOptimizationOptions options)
    {
        if (proposal.Error is not null) return "proposal_error: " + Prefix(proposal.Error, 200);
        if (string.IsNullOrWhiteSpace(proposal.Text) || proposal.Text.Length > options.MaxCandidateCharacters || proposal.Text.Contains('\0'))
            return "invalid_skill_text";
        if (options.RequiredLiterals!.Any(text => !proposal.Text.Contains(text, StringComparison.Ordinal))) return "protected_text_removed";
        if (options.Strategy == SkillOptimizationStrategy.SkillOpt && ChangedSpan(parent.Text, proposal.Text) > options.MaxChangedCharacters)
            return "edit_exceeds_bounded_update";
        return null;
    }
    private static int ChangedSpan(string before, string after)
    {
        var prefix = 0;
        while (prefix < Math.Min(before.Length, after.Length) && before[prefix] == after[prefix]) prefix++;
        var suffix = 0;
        while (suffix < Math.Min(before.Length, after.Length) - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;
        return Math.Max(before.Length, after.Length) - prefix - suffix;
    }
    /// <summary>Validate the exact run configuration without allocating artifacts or calling a model/evaluator.</summary>
    public static void ValidateConfiguration(string initial, IReadOnlyList<SkillOptimizationTask> tasks, SkillOptimizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tasks);
        if (!Enum.IsDefined(options.Strategy) || options.MaxCandidates is < 1 or > 32 || options.MaxProposals is < 0 or > 64 ||
            options.MaxEvaluations is < 1 or > 512 || options.MaxModelCalls is < 1 or > 2048 ||
            options.MaxInputTokens is < 1 or > 1_000_000_000 || options.MaxOutputTokens is < 1 or > 100_000_000 ||
            options.MaxCostUsd is <= 0 or > 10000 || options.TimeoutSeconds is < 1 or > 86400 ||
            options.OperationTimeoutSeconds is < 1 or > 3600 || options.MaxCandidateCharacters is < 32 or > 32000 ||
            options.MaxChangedCharacters is < 1 or > 32000)
            throw new ArgumentException("Optimization limits are outside supported bounds.");
        if (string.IsNullOrWhiteSpace(initial) || initial.Length > options.MaxCandidateCharacters || initial.Contains('\0'))
            throw new ArgumentException("Initial skill must be bounded nonempty text.");
        if (tasks.Count is < 3 or > 64 || tasks.Any(task => task is null || string.IsNullOrWhiteSpace(task.Id) || task.Id.Length > 128 ||
            string.IsNullOrWhiteSpace(task.Family) || task.Family.Length > 128 || !Enum.IsDefined(task.Partition) ||
            task.Payload.ValueKind != JsonValueKind.Object || task.Payload.GetRawText().Length > 16000) ||
            tasks.Select(task => task.Id).Distinct(StringComparer.Ordinal).Count() != tasks.Count)
            throw new ArgumentException("Use 3–64 uniquely identified tasks with bounded object payloads.");
        if (Enum.GetValues<SkillTaskPartition>().Any(partition => !tasks.Any(task => task.Partition == partition)) ||
            tasks.GroupBy(task => task.Family, StringComparer.Ordinal).Any(group => group.Select(task => task.Partition).Distinct().Count() != 1))
            throw new ArgumentException("Train, selection and holdout must be nonempty with disjoint task families.");
        if (tasks.Any(task => tasks.Any(other => other.Partition != task.Partition && JsonElement.DeepEquals(task.Payload, other.Payload))))
            throw new ArgumentException("The same task payload cannot appear in different partitions under different labels.");
        var minimum = tasks.Count + tasks.Count(task => task.Partition == SkillTaskPartition.Holdout);
        if (options.MaxEvaluations < minimum) throw new ArgumentException("Evaluation budget cannot cover baseline development and final held-out comparisons.");
        if (options.RequiredLiterals is { } literals && (literals.Count > 16 || literals.Any(text =>
            string.IsNullOrWhiteSpace(text) || text.Length > 2000 || !initial.Contains(text, StringComparison.Ordinal))))
            throw new ArgumentException("Protected literals must be bounded and occur in the initial skill.");
    }
    private static void ValidateEvaluation(SkillOptimizationTask task, SkillEvaluation result)
    {
        if (result is null || result.TaskId != task.Id || !double.IsFinite(result.Score) || result.Score is < 0 or > 1 ||
            result.Feedback is null || result.Feedback.Length > 100000 || result.Error?.Length > 2000 ||
            (result.TraceSha256 is { } hash && (hash.Length != 64 || hash.Any(ch => !Uri.IsHexDigit(ch)))))
            throw new InvalidDataException("Evaluator returned an invalid result or mismatched task identity.");
    }

    private sealed class OptimizationStop(string status, string message) : Exception(message)
    {
        internal string Status { get; } = status;
    }
    private sealed class Accounting(SkillOptimizationOptions options)
    {
        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private int _modelCalls;
        private long _input, _output;
        private decimal _cost;
        private bool _allTokens = true, _allCosts = true, _allModelCalls = true;
        internal int Proposals { get; private set; }
        internal int Evaluations { get; private set; }
        internal SkillUsageSummary Snapshot() => new(Proposals, Evaluations, _modelCalls, _input, _output, _cost, _allTokens, _allCosts, _allModelCalls);
        internal SkillOperationBudget BeginOperation(bool proposal)
        {
            if (_modelCalls >= options.MaxModelCalls || _input >= options.MaxInputTokens || _output >= options.MaxOutputTokens ||
                (options.MaxCostUsd is { } limit && _cost >= limit) || (proposal ? Proposals >= options.MaxProposals : Evaluations >= options.MaxEvaluations))
                throw new OptimizationStop("budget_exhausted", "The next operation would exceed the remaining invocation or observed-usage budget.");
            var seconds = Math.Min(options.OperationTimeoutSeconds, (int)Math.Floor(options.TimeoutSeconds - _watch.Elapsed.TotalSeconds));
            if (seconds < 1) throw new OptimizationStop("time_limit", "The elapsed-time budget is exhausted.");
            if (proposal) Proposals++; else Evaluations++;
            return new(options.MaxModelCalls - _modelCalls, options.MaxInputTokens - _input, options.MaxOutputTokens - _output,
                options.MaxCostUsd is { } max ? max - _cost : null, seconds);
        }
        internal void MarkUnknownOperation() { _allTokens = false; _allCosts = false; _allModelCalls = false; }
        internal void Add(SkillUsage usage)
        {
            if (usage is null || usage.InputTokens < 0 || usage.OutputTokens < 0 || usage.ModelCalls < 0 || usage.CostUsd < 0)
            {
                MarkUnknownOperation();
                throw new OptimizationStop("invalid_accounting", "Operation returned invalid usage.");
            }
            _modelCalls = checked(_modelCalls + usage.ModelCalls);
            _input = checked(_input + (usage.InputTokens ?? 0));
            _output = checked(_output + (usage.OutputTokens ?? 0));
            _cost += usage.CostUsd ?? 0;
            _allTokens &= usage.InputTokens is not null && usage.OutputTokens is not null;
            _allCosts &= usage.CostUsd is not null || usage.ModelCalls == 0;
            if (!_allTokens || (options.MaxCostUsd is not null && !_allCosts))
                throw new OptimizationStop("accounting_unavailable", "Required token or price accounting was not supplied; no further calls are permitted.");
            if (_modelCalls > options.MaxModelCalls || _input > options.MaxInputTokens || _output > options.MaxOutputTokens ||
                (options.MaxCostUsd is { } limit && _cost > limit))
                throw new OptimizationStop("budget_exceeded", "An in-flight operation exceeded its supplied budget. Recorded actual usage; no further calls are permitted.");
        }
    }

    private sealed class OptimizationArtifacts : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly FileStream _lock;
        internal OptimizationArtifacts(string directory)
        {
            _directory = Path.GetFullPath(directory);
            for (var cursor = new DirectoryInfo(_directory); cursor is not null; cursor = cursor.Parent)
                if (cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Optimization artifacts must not traverse symbolic links or junctions.");
            if (Directory.Exists(_directory) && Directory.EnumerateFileSystemEntries(_directory).Any())
                throw new IOException("Use a new empty artifact directory; existing runs are never overwritten or resumed implicitly.");
            Directory.CreateDirectory(_directory);
            _lock = new FileStream(Path.Combine(_directory, ".run.lock"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        internal async Task CreateManifestAsync(string manifest, SkillCandidate baseline)
        {
            await WriteAsync("tasks.json", manifest);
            await SaveCandidateAsync(baseline);
        }
        internal async Task SaveCandidateAsync(SkillCandidate candidate)
        {
            var folder = Path.Combine(_directory, "candidates", candidate.Id);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "SKILL.md"), candidate.Text, new UTF8Encoding(false, true));
            await File.WriteAllTextAsync(Path.Combine(folder, "candidate.json"), JsonSerializer.Serialize(candidate,
                SkillOptimizationJsonContext.Default.SkillCandidate), new UTF8Encoding(false, true));
        }
        internal Task SaveReportAsync(SkillOptimizationReport report) => WriteAsync("report.json",
            JsonSerializer.Serialize(report, SkillOptimizationJsonContext.Default.SkillOptimizationReport));
        internal Task SaveSelectedAsync(SkillCandidate candidate) => WriteAsync("selected-skill.md", candidate.Text);
        private async Task WriteAsync(string name, string content)
        {
            var temporary = Path.Combine(_directory, ".pending-" + Guid.NewGuid().ToString("N"));
            try
            {
                await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false, true));
                File.Move(temporary, Path.Combine(_directory, name), true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public async ValueTask DisposeAsync() => await _lock.DisposeAsync();
    }
}
