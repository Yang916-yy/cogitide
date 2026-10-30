using System.Text.Json;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class ResearchCommandLineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-research-cli-" + Guid.NewGuid().ToString("N"));
    private string Repository => Path.Combine(_root, "source");

    public ResearchCommandLineTests()
    {
        Directory.CreateDirectory(Path.Combine(Repository, "tests"));
        File.WriteAllText(Path.Combine(Repository, "tests", "validator.txt"), "Frozen validation fixture.\n");
    }

    [Theory]
    [InlineData("--optimize-skill")]
    [InlineData("--optimize-compression")]
    public async Task ValidateOnlyNeedsNoCredentialAndDoesNotRunModelGitOrValidationCommands(string mode)
    {
        // Deliberately not a Git repository. Validation must inspect the specification only.
        var spec = Specification();
        Assert.Null(Environment.GetEnvironmentVariable(spec.Provider.ApiKeyEnvironmentVariable));
        var specification = Path.Combine(_root, "specification.json");
        await File.WriteAllTextAsync(specification, JsonSerializer.Serialize(spec, Wire.Json));
        var output = Path.Combine(_root, "new-output");
        var before = await File.ReadAllTextAsync(Path.Combine(Repository, "tests", "validator.txt"));

        var exit = await ResearchCommandLine.RunAsync([mode, specification, "--output", output, "--validate-only"]);

        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(output));
        Assert.False(Directory.Exists(Path.Combine(Repository, ".git")));
        Assert.Equal(before, await File.ReadAllTextAsync(Path.Combine(Repository, "tests", "validator.txt")));
        Assert.Single(Directory.EnumerateFiles(Repository, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task WindowsValidateOnlyAllowsOutputOnAnotherDriveWithoutCreatingIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        var specification = Path.Combine(_root, "cross-drive.json");
        await File.WriteAllTextAsync(specification, JsonSerializer.Serialize(Specification(), Wire.Json));
        var otherDrive = char.ToUpperInvariant(Path.GetPathRoot(Repository)![0]) == 'Z' ? 'Y' : 'Z';
        var output = otherDrive + ":\\cogitide-dry-validation-" + Guid.NewGuid().ToString("N");

        var exit = await ResearchCommandLine.RunAsync(["--optimize-skill", specification, "--output", output, "--validate-only"]);

        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(output));
    }

    [Theory]
    [InlineData("selection-case")]
    [InlineData("holdout-case")]
    [InlineData("unlisted-case")]
    public void CompressionDiagnosticsCannotRelabelEvaluationCasesAsDevelopment(string caseId)
    {
        var spec = Specification();
        // The text label alone cannot authorize reflective access to held-out diagnostics.
        var leaked = spec with { CompressionFailures = [Failure(caseId)] };
        Assert.ThrowsAny<ArgumentException>(() => ResearchCommandLine.Validate(leaked, "--optimize-compression"));
        ResearchCommandLine.Validate(spec, "--optimize-compression");
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("holdout")]
    public void CompressionTrainingCaseStillRequiresDevelopmentDiagnosticSplit(string split)
    {
        var spec = Specification() with { CompressionFailures = [Failure("train-case") with { DatasetSplit = split }] };
        Assert.ThrowsAny<ArgumentException>(() => ResearchCommandLine.Validate(spec, "--optimize-compression"));
    }

    [Theory]
    [InlineData(SkillTaskPartition.Train)]
    [InlineData(SkillTaskPartition.Selection)]
    [InlineData(SkillTaskPartition.Holdout)]
    public void EveryDatasetPartitionMustRemainNonempty(SkillTaskPartition removed)
    {
        var spec = Specification();
        var replacement = removed == SkillTaskPartition.Train ? SkillTaskPartition.Selection : SkillTaskPartition.Train;
        spec = spec with { Tasks = spec.Tasks.Select(task => task.Partition == removed ? task with { Partition = replacement } : task).ToArray() };
        Assert.Equal(3, spec.Tasks.Count);
        Assert.ThrowsAny<ArgumentException>(() => ResearchCommandLine.Validate(spec, "--optimize-skill"));
    }

    [Theory]
    [InlineData("--optimize-skill")]
    [InlineData("--optimize-compression")]
    public void DifferentTaskIdsDoNotAllowOneProblemFamilyAcrossPartitions(string mode)
    {
        var spec = Specification();
        spec = spec with { Tasks = spec.Tasks.Select(task => task.Partition == SkillTaskPartition.Holdout
            ? task with { Family = spec.Tasks[0].Family } : task).ToArray() };
        Assert.ThrowsAny<ArgumentException>(() => ResearchCommandLine.Validate(spec, mode));
    }

    [Fact]
    public void RenamingTaskAndFamilyDoesNotAllowTheSamePayloadInHoldout()
    {
        var spec = Specification();
        spec = spec with { Tasks = [spec.Tasks[0], spec.Tasks[1], spec.Tasks[2] with { Payload = spec.Tasks[0].Payload.Clone() }] };
        Assert.ThrowsAny<ArgumentException>(() => ResearchCommandLine.Validate(spec, "--optimize-skill"));
    }

    [Theory]
    [InlineData("evaluations")]
    [InlineData("tokens")]
    [InlineData("timeout")]
    [InlineData("request")]
    public void CompressionBudgetsCoverPairedSelectionAndFinalHoldout(string invalid)
    {
        var budget = new CompressionOptimizationBudget(MaxEvaluations: 4);
        budget = invalid switch
        {
            "evaluations" => budget with { MaxEvaluations = 3 },
            "tokens" => budget with { MaxReportedTokens = 0 },
            "timeout" => budget with { TimeoutSeconds = 0 },
            _ => budget with { MaxRequestCharacters = 3999 }
        };
        var spec = Specification() with { CompressionBudget = budget };
        Assert.ThrowsAny<ArgumentException>(() => ResearchCommandLine.Validate(spec, "--optimize-compression"));
    }

    [Theory]
    [InlineData("evaluations")]
    [InlineData("calls")]
    [InlineData("tokens")]
    [InlineData("timeout")]
    public void SkillBudgetsRejectUnmeasurableOrIncompleteBatches(string invalid)
    {
        var options = new SkillOptimizationOptions();
        options = invalid switch
        {
            "evaluations" => options with { MaxEvaluations = 3 },
            "calls" => options with { MaxModelCalls = 0 },
            "tokens" => options with { MaxInputTokens = 0 },
            _ => options with { OperationTimeoutSeconds = 0 }
        };
        var spec = Specification() with { Options = options };
        Assert.ThrowsAny<ArgumentException>(() => ResearchCommandLine.Validate(spec, "--optimize-skill"));
    }

    [Theory]
    [InlineData("remote-http")]
    [InlineData("relative-endpoint")]
    [InlineData("credential-name")]
    [InlineData("missing-credential-name")]
    [InlineData("zero-output")]
    [InlineData("excess-output")]
    [InlineData("empty-model")]
    [InlineData("long-model")]
    [InlineData("unknown-provider")]
    [InlineData("endpoint-credentials")]
    [InlineData("endpoint-query")]
    public void UnsafeOrInvalidModelConfigurationFailsBeforeAnyCredentialIsNeeded(string invalid)
    {
        var spec = Specification();
        var provider = invalid switch
        {
            "remote-http" => spec.Provider with { Endpoint = "http://example.invalid/v1" },
            "relative-endpoint" => spec.Provider with { Endpoint = "v1/responses" },
            "credential-name" => spec.Provider with { ApiKeyEnvironmentVariable = "INVALID-KEY-NAME" },
            "missing-credential-name" => spec.Provider with { ApiKeyEnvironmentVariable = "" },
            "zero-output" => spec.Provider with { MaxOutputTokens = 0 },
            "empty-model" => spec.Provider with { Model = " " },
            "long-model" => spec.Provider with { Model = new string('m', 257) },
            "unknown-provider" => spec.Provider with { Provider = (ProviderKind)999 },
            "endpoint-credentials" => spec.Provider with { Endpoint = "https://fixture:fixture@example.invalid/v1" },
            "endpoint-query" => spec.Provider with { Endpoint = "https://example.invalid/v1?key=fixture" },
            _ => spec.Provider with { MaxOutputTokens = 32769 }
        };
        Assert.ThrowsAny<ArgumentException>(() => ResearchCommandLine.Validate(spec with { Provider = provider }, "--optimize-skill"));
    }

    [Theory]
    [InlineData("missing-pricing")]
    [InlineData("negative-input")]
    [InlineData("negative-output")]
    public void CostBudgetRequiresExplicitNonnegativePricing(string invalid)
    {
        var spec = Specification() with { Options = new(MaxCostUsd: 1) };
        spec = spec with { Pricing = invalid switch
        {
            "negative-input" => new SkillModelPricing(-1, 0),
            "negative-output" => new SkillModelPricing(0, -1),
            _ => null
        } };
        Assert.ThrowsAny<ArgumentException>(() => ResearchCommandLine.Validate(spec, "--optimize-skill"));
        ResearchCommandLine.Validate(spec with { Pricing = new(0, 0) }, "--optimize-skill");
    }

    [Theory]
    [InlineData("")]
    [InlineData("--execute")]
    [InlineData("--optimize-unknown")]
    public void UnknownModeCannotBypassModeSpecificValidation(string mode)
    {
        Assert.ThrowsAny<ArgumentException>(() => ResearchCommandLine.Validate(Specification(), mode));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData(".git/config")]
    public void ProtectedValidationPathsMustStayWithinTheWorkspace(string path)
    {
        var spec = Specification();
        var original = spec.Tasks[0];
        var payload = original.Payload.Deserialize<ResearchTaskPayload>(Wire.Json)! with { ProtectedPaths = [path] };
        spec = spec with { Tasks = [original with { Payload = Wire.Element(payload) }, spec.Tasks[1], spec.Tasks[2]] };
        Assert.Throws<UnauthorizedAccessException>(() => ResearchCommandLine.Validate(spec, "--optimize-skill"));
    }

    private ResearchOptimizationSpec Specification()
    {
        SkillOptimizationTask Task(string id, SkillTaskPartition partition) => new(id, id + "-family", partition,
            Wire.Element(new ResearchTaskPayload(Repository, new string('a', 40), "Repair the independent " + id + " fixture.",
                ["this-command-must-never-run-during-validation"], ["tests/validator.txt"])));
        return new(new ProviderSettings(Endpoint: "http://127.0.0.1:1/v1", Model: "offline-fixture",
                ApiKeyEnvironmentVariable: "COGITIDE_UNSET_FIXTURE_" + Guid.NewGuid().ToString("N"), Provider: ProviderKind.OpenAiCompatible),
            [Task("train-case", SkillTaskPartition.Train), Task("selection-case", SkillTaskPartition.Selection), Task("holdout-case", SkillTaskPartition.Holdout)],
            InitialSkill: "Inspect the current source and verify the requested repair using the frozen checks.",
            CompressionFailures: [Failure("train-case")], CompressionBudget: new(MaxEvaluations: 4));
    }

    private static CompressionFailureDiagnostic Failure(string id) => new(id, "development",
        "The original observation contains a failed assertion and an unfinished source repair.",
        "The compressed observation incorrectly says verification succeeded.", "The failure marker was lost.");

    public void Dispose()
    {
        var owned = Path.GetFullPath(_root);
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!owned.StartsWith(temporary, comparison) || !Path.GetFileName(owned).StartsWith("cogitide-research-cli-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected fixture cleanup path.");
        if (Directory.Exists(owned)) Directory.Delete(owned, true);
    }
}
