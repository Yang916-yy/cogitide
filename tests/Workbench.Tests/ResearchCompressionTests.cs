using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class ResearchCompressionTests
{
    private const string Summary = "## Goal\nFinish the requested repair.\n## Progress\nAn earlier check failed; completion remains unverified.\n## Next Steps\nInspect current evidence before rerunning verification.";

    private static List<ChatMessage> History(int groups = 6)
    {
        List<ChatMessage> messages = [new("user", "Keep public signatures; never publish or remove the original test.")];
        for (var i = 0; i < groups; i++)
        {
            messages.Add(new("assistant", "Inspect the generated fixture.", [new("read-" + i, "read_file", "{\"path\":\"src/Calculator.cs\"}")],
                Continuation: new("fixture", "https://fixture.invalid", "fixture", Wire.Element(new { privateReasoning = "DO_NOT_COMPRESS_PRIVATE_REASONING" }))));
            messages.Add(new("tool", new string('x', 2100) + "\nERROR_CODE=E42 src/Calculator.cs: preserve pending parser fix\n" + new string('y', 2100),
                ToolCallId: "read-" + i, ToolIsError: true));
        }
        return messages;
    }

    [Fact]
    public void DisabledGuidancePreservesBaselinePlanAndObservationObjects()
    {
        var history = History();
        var projection = CompressionGuidance.ProjectObservations(history, null);
        Assert.Same(history, projection.Messages);
        Assert.Equal(0, projection.ReducedCount);
        var baseline = ConversationCompaction.Prepare("System.", history, null, 20000, history.Count);
        var disabled = ConversationCompaction.Prepare("System.", history, null, 20000, history.Count, null);
        Assert.NotNull(baseline);
        Assert.Equal(baseline, disabled);
    }

    [Fact]
    public void ObservationCompressionCanAvoidAnUnnecessaryHistoryModelCall()
    {
        var history = History();
        Assert.NotNull(ConversationCompaction.Prepare("System.", history, null, 20000, history.Count));
        Assert.Null(ConversationCompaction.Prepare("System.", history, null, 20000, history.Count, CompressionGuidance.Default));
    }

    [Fact]
    public async Task GuidedSummaryReceivesVersionedPolicyAndPreservesExactSourceSignals()
    {
        var history = History(20);
        var original = history.Select(message => message.Content).ToArray();
        var guidance = CompressionGuidance.Default with { HistoryInstructions = "Keep ERROR_CODE diagnostics and exact pending source paths." };
        var plan = ConversationCompaction.Prepare("System.", history, null, 20000, history.Count, guidance);
        Assert.NotNull(plan);
        var model = new Model(messages =>
        {
            using var payload = JsonDocument.Parse(messages[1].Content);
            Assert.Equal(guidance.HistoryInstructions, payload.RootElement.GetProperty("compressionGuidance").GetProperty("historyInstructions").GetString());
            Assert.DoesNotContain("DO_NOT_COMPRESS_PRIVATE_REASONING", messages[1].Content);
            Assert.Contains("compressed-observation", plan.Transcript);
            Assert.True(ContextWindow.Size(messages) <= 20000);
            return Summary;
        });
        var result = await ConversationCompaction.GenerateAsync(plan, model, CancellationToken.None);
        Assert.Contains("## Source anchors", result.Checkpoint.Summary);
        Assert.Contains("ERROR_CODE=E42", result.Checkpoint.Summary);
        Assert.Contains("src/Calculator.cs", result.Checkpoint.Summary);
        Assert.Contains(guidance.Digest(), result.Checkpoint.Summary);
        Assert.True(ConversationCompaction.IsValid(result.Checkpoint, history));
        Assert.True(result.Checkpoint.Summary.Length <= plan.SummaryLimit);
        Assert.Equal(original, history.Select(message => message.Content));
        Assert.Contains(ConversationCompaction.Project(history, result.Checkpoint, history.Count), message => message.Content == original[0]);
        Assert.Equal(1, model.Calls);
    }

    [Fact]
    public void ObservationProjectionPreservesCursorIdentityAndRawJournal()
    {
        var raw = JsonSerializer.Serialize(new { commandId = "owned-command", status = "running", running = true,
            nextOffset = 12345, hasMore = true, outputTruncated = false, output = new string('x', 5000) + "😀中文" + new string('y', 5000) }, Wire.Json);
        ChatMessage[] messages = [new("assistant", "", [new("owned", "read_command", "{}")]), new("tool", raw, ToolCallId: "owned", ToolIsError: false)];
        var projected = CompressionGuidance.ProjectObservations(messages, CompressionGuidance.Default);
        Assert.Equal(1, projected.ReducedCount);
        Assert.True(projected.SavedCharacters > 0);
        Assert.Equal(raw, messages[1].Content);
        Assert.Equal(messages[0], projected.Messages[0]);
        Assert.Equal("owned", projected.Messages[1].ToolCallId);
        using var doc = JsonDocument.Parse(projected.Messages[1].Content);
        Assert.Equal("compressed-observation", doc.RootElement.GetProperty("projectionKind").GetString());
        Assert.Equal("owned-command", doc.RootElement.GetProperty("metadata").GetProperty("commandId").GetString());
        Assert.Equal(12345, doc.RootElement.GetProperty("metadata").GetProperty("nextOffset").GetInt32());
        Assert.True(doc.RootElement.GetProperty("metadata").GetProperty("running").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("omitted").GetBoolean());
        Assert.InRange(projected.Messages[1].Content.Length, 1, CompressionGuidance.Default.ObservationCharacterLimit);
    }

    [Fact]
    public void UnicodeExcerptAndAnchorsContainNoSplitSurrogates()
    {
        var raw = new string('a', 499) + "😀" + new string('b', 4000) + "\nERROR: src/解析器.cs still pending\n" + new string('c', 499) + "🏁";
        var excerpt = CompressionSignals.Excerpt(raw, 1000);
        _ = new UTF8Encoding(false, true).GetBytes(excerpt);
        var selection = CompressionSignals.Select([new("tool", raw, ToolCallId: "unicode", ToolIsError: true)], 1, 1000);
        Assert.Contains(selection.Anchors, anchor => anchor.Value.Contains("src/解析器.cs", StringComparison.Ordinal));
        var appendix = CompressionSignals.Appendix(CompressionGuidance.Default, selection);
        _ = new UTF8Encoding(false, true).GetBytes(appendix);
    }

    [Fact]
    public async Task FailedGuidedSummaryKeepsExistingCheckpointAndOriginalMessages()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cogito-guidance-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using var store = new SqliteSessionStore(Path.Combine(folder, "sessions.db"));
            var history = History(20);
            var checkpoint = new CompactionCheckpoint(1, 3, ConversationCompaction.Digest(history, 3), Summary, DateTimeOffset.UtcNow);
            var id = Guid.NewGuid().ToString("N");
            var now = DateTimeOffset.UtcNow;
            await store.SaveAsync(new(new(id, "guidance", folder, now, now), history, checkpoint));
            var model = new Model(messages => messages[0].Content.StartsWith("You summarize", StringComparison.Ordinal)
                ? throw new IOException("Generated compressor failure") : "Only reporting observations.");
            await new AgentEngine(store, []).RunAsync(id, "Report progress only.", model,
                new(MaxContextCharacters: 20000, Research: new(CompressionGuidance: true)), (_, _) => ValueTask.CompletedTask,
                (_, _) => Task.FromResult(false));
            var saved = (await store.LoadAsync(id))!;
            Assert.Equal(checkpoint, saved.Compaction);
            Assert.Equal(history.Select(message => message.Content), saved.Messages.Take(history.Count).Select(message => message.Content));
            Assert.Contains(await store.EventsAsync(id), evt => evt.Type == "context.compaction_failed");
            Assert.DoesNotContain(await store.EventsAsync(id), evt => evt.Type == "context.compacted");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); try { Directory.Delete(folder, true); } catch (IOException) { } }
    }

    [Theory]
    [InlineData(0, 2000)]
    [InlineData(2, 2000)]
    [InlineData(1, 799)]
    [InlineData(1, 12001)]
    public void InvalidPolicyFailsBeforeModelExecution(int schema, int observationLimit)
    {
        var guidance = CompressionGuidance.Default with { SchemaVersion = schema, ObservationCharacterLimit = observationLimit };
        Assert.Throws<InvalidDataException>(() => ConversationCompaction.Prepare("system", History(), null, 20000, 0, guidance));
    }

    [Fact]
    public async Task GuidelineCanBeSavedLoadedAndRejectsOversizedOrUnknownShape()
    {
        var directory = Path.Combine(Path.GetTempPath(), "cogito-guideline-profile-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "guideline.json");
        try
        {
            await CompressionGuidance.Default.SaveAsync(path);
            Assert.Equal(CompressionGuidance.Default, await CompressionGuidance.LoadAsync(path));
            await File.WriteAllTextAsync(path, new string('x', 65537));
            await Assert.ThrowsAsync<InvalidDataException>(() => CompressionGuidance.LoadAsync(path));
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"unexpected\":true}");
            await Assert.ThrowsAsync<JsonException>(() => CompressionGuidance.LoadAsync(path));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static CompressionFailureDiagnostic Failure(string split = "development") => new("dev-one", split,
        "The original result contains ERROR_CODE=E42 and the pending src/Calculator.cs fix.",
        "The summary says checks ran, without their outcome.", "The downstream agent treated a failed check as completed.");

    private static string ProposalJson => JsonSerializer.Serialize(new
    {
        historyInstructions = "Preserve ERROR_CODE failure outcomes and pending repair paths before compressing successful log noise.",
        observationInstructions = "Retain exit status, source hashes and output cursors; preserve exact error signatures.",
        observationCharacterLimit = 1800,
        rationale = "The development failure lost an explicit failing check, so retain that signal before reducing context."
    }, Wire.Json);

    private static Task<CompressionGuidanceProposal> Proposal() => CompressionGuidanceOptimizer.ProposeAsync(
        CompressionGuidance.Default, [Failure()], new Model(_ => ProposalJson));

    [Fact]
    public async Task FailureDrivenProposalAndPairedSelectionProduceDeployableVersion()
    {
        var model = new Model(messages =>
        {
            Assert.Contains("originalContext", messages[1].Content);
            Assert.Contains("ERROR_CODE=E42", messages[1].Content);
            Assert.Contains("downstream agent", messages[1].Content);
            return ProposalJson;
        });
        var proposal = await CompressionGuidanceOptimizer.ProposeAsync(CompressionGuidance.Default, [Failure()], model);
        Assert.Equal(1, model.Calls);
        Assert.Equal(2, proposal.Guidance.Revision);
        var observed = new List<string>();
        var comparison = await CompressionGuidanceOptimizer.CompareAsync(CompressionGuidance.Default, proposal,
            [new("select-one", "selection"), new("select-two", "selection")], (guideline, task, _) =>
            {
                observed.Add(task.Id + ":" + guideline.Revision);
                return Task.FromResult(new CompressionCaseScore(task.Id, true, true, guideline.Revision == 1 ? 5000 : 3000, 100, 20));
            });
        Assert.True(comparison.Accepted);
        Assert.Equal(proposal.Guidance, comparison.Selected);
        Assert.Equal(new[] { "select-one:1", "select-one:2", "select-two:1", "select-two:2" }, observed);
        Assert.Equal(600, comparison.ReportedTokens);
        Assert.Contains("equal task utility", comparison.Reason);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SmallerContextCannotHideTaskOrRequiredSignalRegression(bool taskSuccess, bool signals)
    {
        var proposal = await Proposal();
        var comparison = await CompressionGuidanceOptimizer.CompareAsync(CompressionGuidance.Default, proposal,
            [new("select-one", "selection")], (guideline, task, _) => Task.FromResult(new CompressionCaseScore(task.Id,
                guideline.Revision == 1 || taskSuccess, guideline.Revision == 1 || signals, guideline.Revision == 1 ? 5000 : 1000, 0, 0)));
        Assert.False(comparison.Accepted);
        Assert.Same(CompressionGuidance.Default, comparison.Selected);
    }

    [Theory]
    [InlineData("final-test", "held-out")]
    [InlineData("development", "dev-two")]
    [InlineData("selection", "dev-one")]
    public async Task SelectionCannotUseHeldOutOrDevelopmentCases(string split, string id)
    {
        var proposal = await Proposal();
        await Assert.ThrowsAsync<ArgumentException>(() => CompressionGuidanceOptimizer.CompareAsync(CompressionGuidance.Default, proposal,
            [new(id, split)], (_, _, _) => throw new InvalidOperationException("Must reject before evaluation.")));
    }

    [Fact]
    public async Task RefinementCannotReadFinalTestFailureOrRunTools()
    {
        var model = new Model(_ => ProposalJson);
        await Assert.ThrowsAsync<ArgumentException>(() => CompressionGuidanceOptimizer.ProposeAsync(CompressionGuidance.Default, [Failure("final-test")], model));
        Assert.Equal(0, model.Calls);
        await Assert.ThrowsAsync<InvalidDataException>(() => CompressionGuidanceOptimizer.ProposeAsync(CompressionGuidance.Default, [Failure()], new ToolModel()));
    }

    [Fact]
    public async Task IncompleteUsageCannotBeTreatedAsFreeGuidelineOptimization()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => CompressionGuidanceOptimizer.ProposeAsync(
            CompressionGuidance.Default, [Failure()], new MissingUsageModel()));
    }

    [Fact]
    public void OfflineConfigurationValidationRejectsLeakageBeforeAnyWork()
    {
        CompressionGuidanceOptimizer.ValidateConfiguration(CompressionGuidance.Default, [Failure()], [new("selection-one", "selection")]);
        Assert.Throws<ArgumentException>(() => CompressionGuidanceOptimizer.ValidateConfiguration(CompressionGuidance.Default,
            [Failure("final-test")], [new("selection-one", "selection")]));
        Assert.Throws<ArgumentException>(() => CompressionGuidanceOptimizer.ValidateConfiguration(CompressionGuidance.Default,
            [Failure()], [new("dev-one", "selection")]));
        Assert.Throws<ArgumentException>(() => CompressionGuidanceOptimizer.ValidateConfiguration(CompressionGuidance.Default,
            [Failure()], []));
        Assert.Throws<ArgumentException>(() => CompressionGuidanceOptimizer.ValidateConfiguration(CompressionGuidance.Default,
            [Failure()], [new("one", "selection"), new("two", "selection")], new(MaxEvaluations: 2)));
    }

    [Fact]
    public async Task ExactBudgetExhaustionDoesNotStartAnotherEvaluation()
    {
        var proposal = await Proposal();
        var allowances = new List<long>();
        await Assert.ThrowsAsync<InvalidDataException>(() => CompressionGuidanceOptimizer.CompareAsync(CompressionGuidance.Default, proposal,
            [new("selection", "selection")], (_, task, remaining, _) =>
            {
                allowances.Add(remaining);
                return Task.FromResult(new CompressionCaseScore(task.Id, true, true, 1000, remaining, 0));
            }, new(MaxReportedTokens: 220)));
        Assert.Equal(new long[] { 100 }, allowances);
    }

    [Fact]
    public async Task BudgetStopsFurtherEvaluationAndBaselineIdentityMustMatch()
    {
        var proposal = await Proposal();
        var count = 0;
        await Assert.ThrowsAsync<InvalidDataException>(() => CompressionGuidanceOptimizer.CompareAsync(CompressionGuidance.Default, proposal,
            [new("s1", "selection"), new("s2", "selection"), new("s3", "selection")], (_, task, _) =>
            {
                count++;
                return Task.FromResult(new CompressionCaseScore(task.Id, true, true, 1000, 100, 0));
            }, new(MaxReportedTokens: 500)));
        Assert.Equal(4, count);
        await Assert.ThrowsAsync<InvalidDataException>(() => CompressionGuidanceOptimizer.CompareAsync(
            CompressionGuidance.Default with { Revision = 2 }, proposal, [new("s1", "selection")],
            (_, _, _) => throw new InvalidOperationException("Must reject before evaluation.")));
    }

    private sealed class Model(Func<IReadOnlyList<ChatMessage>, string> response) : IAgentModel
    {
        public int Calls { get; private set; }
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(TextDelta: response(messages), InputTokens: 100, OutputTokens: 20);
            await Task.CompletedTask;
        }
    }
    private sealed class ToolModel : IAgentModel
    {
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(ToolCalls: [new("bad", "run_command", "{}")]);
            await Task.CompletedTask;
        }
    }
    private sealed class MissingUsageModel : IAgentModel
    {
        public async IAsyncEnumerable<ModelUpdate> StreamAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(TextDelta: ProposalJson);
            await Task.CompletedTask;
        }
    }
}
