using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workbench.Core;

public enum SkillOptimizationStrategy { Gepa, SkillOpt }
public enum SkillTaskPartition { Train, Selection, Holdout }

/// <summary>Payload belongs to the evaluator. Families must not cross partition boundaries.</summary>
public sealed record SkillOptimizationTask(string Id, string Family, SkillTaskPartition Partition, JsonElement Payload);
public sealed record SkillCandidate(string Id, string Text, string Sha256, IReadOnlyList<string> ParentIds, int Generation = 0);
public sealed record SkillUsage(long? InputTokens = 0, long? OutputTokens = 0, int ModelCalls = 0, decimal? CostUsd = null);
public sealed record SkillOperationBudget(int MaxModelCalls, long MaxInputTokens, long MaxOutputTokens,
    decimal? MaxCostUsd, int TimeoutSeconds);
public sealed record SkillEvaluation(string TaskId, double Score, bool ConstraintsSatisfied, string Feedback,
    SkillUsage Usage, string? TraceSha256 = null, string? Error = null);
public sealed record SkillTrainingObservation(string TaskId, string Family, JsonElement Payload, double Score,
    bool ConstraintsSatisfied, string Feedback);
public sealed record SkillProposalRequest(SkillOptimizationStrategy Strategy, int Round,
    IReadOnlyList<SkillCandidate> Parents, IReadOnlyList<SkillTrainingObservation> Observations,
    int MaximumCharacters, int MaximumChangedCharacters, IReadOnlyList<string> RequiredLiterals);
public sealed record SkillProposal(string Text, string Reflection, SkillUsage Usage, string? Error = null);

/// <summary>
/// Executes a frozen task using the candidate. The caller must isolate each run, freeze validators,
/// enforce the supplied caps between model calls, and include summary/reflection usage in its result.
/// Score must come from the evaluator's frozen checks, not the candidate model's self-assessment.
/// </summary>
public interface ISkillCandidateEvaluator
{
    Task<SkillEvaluation> EvaluateAsync(SkillCandidate candidate, SkillOptimizationTask task,
        SkillOperationBudget budget, CancellationToken cancellationToken = default);
}

public interface ISkillCandidateProposer
{
    Task<SkillProposal> ProposeAsync(SkillProposalRequest request, SkillOperationBudget budget,
        CancellationToken cancellationToken = default);
}

public sealed record SkillOptimizationOptions(
    SkillOptimizationStrategy Strategy = SkillOptimizationStrategy.SkillOpt,
    int MaxCandidates = 8, int MaxProposals = 8, int MaxEvaluations = 64, int MaxModelCalls = 64,
    long MaxInputTokens = 500_000, long MaxOutputTokens = 100_000, decimal? MaxCostUsd = null,
    int TimeoutSeconds = 600, int OperationTimeoutSeconds = 120, int MaxCandidateCharacters = 12_000,
    int MaxChangedCharacters = 4_000, int Seed = 17, bool EnableMerge = false,
    IReadOnlyList<string>? RequiredLiterals = null);

public sealed record SkillCandidateEvaluation(string CandidateId, SkillTaskPartition Partition, SkillEvaluation Result);
public sealed record SkillOptimizationDecision(int Round, IReadOnlyList<string> ParentIds, string? CandidateId,
    string Action, string Reason, string Reflection);
public sealed record SkillUsageSummary(int ProposalCalls, int EvaluationCalls, int ModelCalls,
    long KnownInputTokens, long KnownOutputTokens, decimal KnownCostUsd, bool AllTokenUsageKnown, bool AllCostsKnown,
    bool AllModelCallsKnown = true);
public sealed record SkillOptimizationReport(int Version, string RunId, SkillOptimizationStrategy Strategy,
    string Status, string TaskManifestSha256, string InitialSkillSha256, string SelectedCandidateId,
    string SelectedSkillSha256, IReadOnlyList<SkillCandidate> Candidates,
    IReadOnlyList<SkillCandidateEvaluation> Evaluations, IReadOnlyList<SkillOptimizationDecision> Decisions,
    IReadOnlyList<string> FrontierIds, SkillUsageSummary Usage, SkillOptimizationOptions Options,
    DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool HoldoutCompleted, string? Error = null,
    bool AutomaticallyActivated = false);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(SkillOptimizationReport))]
[JsonSerializable(typeof(SkillOptimizationTask[]))]
[JsonSerializable(typeof(SkillCandidate))]
internal partial class SkillOptimizationJsonContext : JsonSerializerContext;
