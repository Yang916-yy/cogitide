using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class EvidenceToolUpgradeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cogito-evidence-upgrade-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Combine(_directory, "workspace");
    private static readonly string FileHash = new('a', 64);
    private const string CommandId = "0123456789abcdef0123456789abcdef";

    public EvidenceToolUpgradeTests() => Directory.CreateDirectory(Workspace);
    private EvidenceMemory Ledger() => new("session", Workspace);
    private static AgentEvent Completed(long sequence, string name, object args, object result, bool error = false) =>
        new("session", "run", "tool.completed", Call: new("call-" + sequence, name, JsonSerializer.Serialize(args, Wire.Json)),
            Result: new(JsonSerializer.Serialize(result, Wire.Json), error), Sequence: sequence);
    private static object CommandResult(string status, int? exitCode, bool running, string id = CommandId,
        bool outputTruncated = false) => new { commandId = id, command = "dotnet test", status, exitCode, running,
            output = "observed output", nextOffset = 15, outputTruncated };

    [Theory]
    [InlineData("starting", null, true, false, EvidenceOutcome.Observation)]
    [InlineData("running", null, true, false, EvidenceOutcome.Observation)]
    [InlineData("completed", 0, false, false, EvidenceOutcome.CommandExitedZero)]
    [InlineData("completed", 2, false, false, EvidenceOutcome.CommandFailed)]
    [InlineData("completed", 0, false, true, EvidenceOutcome.ToolError)]
    [InlineData("failed", null, false, true, EvidenceOutcome.ToolError)]
    [InlineData("cancelled", 0, false, true, EvidenceOutcome.Unknown)]
    [InlineData("interrupted", 0, false, true, EvidenceOutcome.Unknown)]
    [InlineData("timed_out", 0, false, true, EvidenceOutcome.TimedOut)]
    public void CommandPollDistinguishesRunningAndTerminalEvidence(string status, int? code, bool running, bool error, EvidenceOutcome expected)
    {
        var ledger = Ledger();
        ledger.Observe(Completed(1, "read_command", new { commandId = CommandId }, CommandResult(status, code, running), error));
        Assert.Equal(expected, Assert.Single(ledger.Snapshot().Entries).Outcome);
    }

    [Fact]
    public void MismatchedAndContradictoryCommandEnvelopesNeverVerify()
    {
        object[] invalid =
        [
            CommandResult("completed", 0, false, "another-command"),
            CommandResult("running", 0, true),
            CommandResult("completed", 0, true),
            CommandResult("completed", null, false),
            CommandResult("unrecognized", 0, false),
            new { commandId = CommandId, command = "dotnet test", status = "completed", exitCode = 0, running = false,
                output = "", nextOffset = "0", outputTruncated = false },
            new { commandId = CommandId, command = "dotnet test", status = "completed", exitCode = 0, running = false,
                output = "", nextOffset = 0, outputTruncated = false, timedOut = "false" }
        ];
        foreach (var result in invalid)
        {
            var ledger = Ledger();
            ledger.Observe(Completed(1, "read_command", new { commandId = CommandId }, result));
            Assert.Equal(EvidenceOutcome.Unknown, Assert.Single(ledger.Snapshot().Entries).Outcome);
        }
        var start = Ledger();
        start.Observe(Completed(1, "start_command", new { command = "different command" }, CommandResult("completed", 0, false)));
        Assert.Equal(EvidenceOutcome.Unknown, Assert.Single(start.Snapshot().Entries).Outcome);
    }

    [Theory]
    [InlineData("apply_patch")]
    [InlineData("start_command")]
    [InlineData("read_command")]
    [InlineData("stop_command")]
    public void NewMutatingOperationsInvalidateOnStartAndCompletionEvenForErrors(string name)
    {
        var ledger = Ledger();
        ledger.Observe(Completed(1, "read_file", new { path = "a.cs" }, new { path = "a.cs", sha256 = FileHash }));
        ledger.Observe(new("session", "run", "tool.started", Call: new("mutation", name, "{}"), Sequence: 2));
        Assert.Equal(EvidenceValidity.Stale, Assert.Single(ledger.Snapshot().Entries).Validity);
        ledger.Observe(Completed(3, "read_file", new { path = "b.cs" }, new { path = "b.cs", sha256 = FileHash }));
        ledger.Observe(Completed(4, name, new { }, new { error = "failed" }, true));
        Assert.Equal(EvidenceValidity.Stale, ledger.Snapshot().Entries[1].Validity);
        Assert.Equal(2, ledger.Snapshot().WorkspaceEpoch);
    }

    [Fact]
    public void PollPreservesOutputTruncationWithoutTreatingItAsFailureOrCompleteOutput()
    {
        var ledger = Ledger();
        ledger.Observe(Completed(1, "read_command", new { commandId = CommandId }, CommandResult("completed", 0, false, outputTruncated: true)));
        var evidence = Assert.Single(ledger.Snapshot().Entries);
        Assert.Equal(EvidenceOutcome.CommandExitedZero, evidence.Outcome);
        Assert.True(evidence.SourceTruncated);
    }

    [Fact]
    public async Task ActualGlobEnvelopeIsObservedButMismatchedPatternAndEscapingPathsAreNot()
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.cs"), "class A {}");
        var result = await new WorkspaceService().GlobFilesAsync(Workspace, "**/*.cs");
        Assert.False(result.IsError, result.Content);
        var evt = new AgentEvent("session", "run", "tool.completed", Call: new("glob", "glob_files", "{\"pattern\":\"**/*.cs\"}"), Result: result, Sequence: 1);
        var ledger = Ledger(); ledger.Observe(evt);
        Assert.Equal(EvidenceOutcome.Observation, Assert.Single(ledger.Snapshot().Entries).Outcome);
        foreach (var content in new[]
        {
            result.Content.Replace("**/*.cs", "**/*.txt", StringComparison.Ordinal),
            result.Content.Replace("a.cs", "../outside.cs", StringComparison.Ordinal)
        })
        {
            var invalid = Ledger(); invalid.Observe(evt with { Result = new(content) });
            Assert.Equal(EvidenceOutcome.Unknown, Assert.Single(invalid.Snapshot().Entries).Outcome);
        }
    }

    [Theory]
    [InlineData("modified target")]
    [InlineData("recreated deleted file")]
    [InlineData("recreated moved source")]
    [InlineData("directory at deleted path")]
    public async Task MultiFilePatchEvidenceSurvivesRestartAndChecksEveryTargetAndDeletion(string mutation)
    {
        var original = "old\n";
        foreach (var path in new[] { "modify.txt", "remove.txt", "move.txt" })
            await File.WriteAllTextAsync(Path.Combine(Workspace, path), original);
        const string patch = "*** Begin Patch\n*** Update File: modify.txt\n@@\n-old\n+new\n*** Delete File: remove.txt\n*** Update File: move.txt\n*** Move to: moved.txt\n*** Add File: added.txt\n+added\n*** End Patch";
        var hashes = new Dictionary<string, string> { ["modify.txt"] = Hash(original), ["remove.txt"] = Hash(original), ["move.txt"] = Hash(original) };
        var result = await new WorkspaceService().ApplyPatchAsync(Workspace, patch, hashes);
        Assert.False(result.IsError, result.Content);
        var database = Path.Combine(_directory, "sessions.db");
        using (var store = new SqliteSessionStore(database))
        {
            var at = DateTimeOffset.UtcNow;
            await store.SaveAsync(new(new("session", "Fixture", Workspace, at, at), []));
            var evt = await store.AppendEventAsync(new("session", "run", "tool.completed",
                Call: new("patch", "apply_patch", JsonSerializer.Serialize(new { patchText = patch, expectedHashes = hashes }, Wire.Json)), Result: result));
            var ledger = Ledger(); ledger.Observe(evt);
            var observation = Assert.Single(ledger.Snapshot().Entries);
            Assert.Equal(EvidenceOutcome.Observation, observation.Outcome);
            Assert.Equal(5, observation.Artifacts!.Count);
            Assert.Equal(2, observation.Artifacts.Count(artifact => artifact.Deleted));
            await Remember(store, evt.Sequence);
        }
        using var reopened = new SqliteSessionStore(database);
        var tools = new ProjectExperienceTools(reopened, "session", "recall", Workspace, WorkspaceService.Resolve);
        var before = Assert.Single((await tools.RecallAsync("Patch")).Matches);
        Assert.Equal("ReportedFilesMatchOnly", before.ArtifactStatus);
        Assert.Equal(5, Assert.Single(before.Experience.Sources).Artifacts!.Count);
        if (mutation == "directory at deleted path") Directory.CreateDirectory(Path.Combine(Workspace, "remove.txt"));
        else await File.WriteAllTextAsync(Path.Combine(Workspace, mutation switch
        {
            "modified target" => "moved.txt", "recreated deleted file" => "remove.txt", _ => "move.txt"
        }), "externally changed");
        var after = Assert.Single((await tools.RecallAsync("Patch")).Matches);
        Assert.Equal("Intact", after.SourceStatus);
        Assert.Equal("ChangedOrUnavailable", after.ArtifactStatus);
        Assert.Equal("Candidate", after.Experience.Status);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("operation")]
    [InlineData("hash")]
    [InlineData("duplicate")]
    public void PatchReportsMustMatchDeclaredPathsOperationsAndPreconditions(string mismatch)
    {
        const string patch = "*** Begin Patch\n*** Delete File: deleted.txt\n*** End Patch";
        var change = new { path = mismatch == "path" ? "../outside.txt" : "deleted.txt", operation = mismatch == "operation" ? "update" : "delete",
            previousSha256 = mismatch == "hash" ? new string('b', 64) : FileHash, sha256 = (string?)null };
        var ledger = Ledger();
        ledger.Observe(Completed(1, "apply_patch", new { patchText = patch, expectedHashes = new Dictionary<string, string> { ["deleted.txt"] = FileHash } },
            new { changed = true, changes = mismatch == "duplicate" ? new[] { change, change } : [change] }));
        var observation = Assert.Single(ledger.Snapshot().Entries);
        Assert.Equal(EvidenceOutcome.Unknown, observation.Outcome);
        Assert.Null(observation.Artifacts);
    }

    [Theory]
    [InlineData(2 * 1024 * 1024, "ReportedFilesMatchOnly")]
    [InlineData(16 * 1024 * 1024, "ReportedFilesMatchOnly")]
    [InlineData(16 * 1024 * 1024 + 1, "ChangedOrUnavailable")]
    public async Task FullFileFreshnessSupportsBoundedLargeReadPages(int length, string expected)
    {
        var data = new byte[length]; Array.Fill(data, (byte)'x');
        await File.WriteAllBytesAsync(Path.Combine(Workspace, "large.txt"), data);
        using var store = new SqliteSessionStore(Path.Combine(_directory, "sessions.db"));
        var at = DateTimeOffset.UtcNow;
        await store.SaveAsync(new(new("session", "Fixture", Workspace, at, at), []));
        var evt = await store.AppendEventAsync(Completed(1, "read_file", new { path = "large.txt" },
            new { path = "large.txt", sha256 = Convert.ToHexStringLower(SHA256.HashData(data)), content = "a page", truncated = true }) with { Sequence = 0 });
        await Remember(store, evt.Sequence);
        var recall = new ProjectExperienceTools(store, "session", "recall", Workspace, WorkspaceService.Resolve);
        Assert.Equal(expected, Assert.Single((await recall.RecallAsync("Patch")).Matches).ArtifactStatus);
        await File.AppendAllTextAsync(Path.Combine(Workspace, "large.txt"), "changed");
        Assert.Equal("ChangedOrUnavailable", Assert.Single((await recall.RecallAsync("Patch")).Matches).ArtifactStatus);
    }

    [Fact]
    public void OldArtifactAndSourceJsonRemainUnchangedAndReadable()
    {
        var artifact = new EvidenceArtifact("a.cs", FileHash);
        var json = JsonSerializer.Serialize(artifact, Wire.Json);
        Assert.Equal("{\"workspaceRelativePath\":\"a.cs\",\"reportedSha256\":\"" + FileHash + "\"}", json);
        var source = new ProjectExperienceSource(1, FileHash, "read_file", FileHash, artifact);
        var sourceJson = JsonSerializer.Serialize(source, Wire.Json);
        Assert.DoesNotContain("artifacts", sourceJson, StringComparison.Ordinal);
        var restored = JsonSerializer.Deserialize<ProjectExperienceSource>(sourceJson, Wire.Json)!;
        Assert.Null(restored.Artifacts);
        Assert.Equal(artifact, restored.Artifact);
    }

    [Theory]
    [InlineData(12, false)]
    [InlineData(20, true)]
    public async Task LargePatchCandidateIsEitherFullyRecallableOrRejectedBeforeSaving(int fileCount, bool rejected)
    {
        var prefix = string.Join('/', Enumerable.Repeat(new string('a', 90), 8));
        var paths = Enumerable.Range(0, fileCount).Select(index => prefix + "/file-" + index + ".txt").ToArray();
        var patch = "*** Begin Patch\n" + string.Concat(paths.Select(path => "*** Add File: " + path + "\n+content\n")) + "*** End Patch";
        using var store = new SqliteSessionStore(Path.Combine(_directory, "sessions.db"));
        var at = DateTimeOffset.UtcNow;
        await store.SaveAsync(new(new("session", "Fixture", Workspace, at, at), []));
        var source = await store.AppendEventAsync(Completed(1, "apply_patch",
            new { patchText = patch, expectedHashes = new Dictionary<string, string>() },
            new { changed = true, changes = paths.Select(path => new
                { path, operation = "add", previousSha256 = (string?)null, sha256 = FileHash }).ToArray() }) with { Sequence = 0 });
        var ledger = Ledger(); ledger.Observe(source);
        Assert.Equal(fileCount, Assert.Single(ledger.Snapshot().Entries).Artifacts!.Count);
        var memory = new ProjectExperienceTools(store, "session", "remember", Workspace, WorkspaceService.Resolve);
        var tool = memory.CreateTools().Single(tool => tool.Definition.Name == ProjectExperienceTools.RememberName);
        var saved = await tool.ExecuteAsync(new("remember", tool.Definition.Name, JsonSerializer.Serialize(new
            { title = "Large patch", preconditions = "Inspect files", procedure = "Review changes", evidenceSequences = new[] { source.Sequence } }, Wire.Json)), new(Workspace));
        Assert.Equal(rejected, saved.IsError);
        if (rejected)
        {
            Assert.Contains("recall response", saved.Content, StringComparison.Ordinal);
            Assert.DoesNotContain(await store.EventsAsync("session"), evt => evt.Type == "experience.saved");
        }
        else
        {
            var recalled = await memory.RecallAsync("Large patch");
            var match = Assert.Single(recalled.Matches);
            Assert.Equal(fileCount, Assert.Single(match.Experience.Sources).Artifacts!.Count);
            Assert.Equal("Intact", match.SourceStatus);
            Assert.False(recalled.Truncated);
            Assert.InRange(JsonSerializer.Serialize(recalled, Wire.Json).Length, 1, 16_000);
        }
    }

    private async Task Remember(ISessionStore store, long sequence)
    {
        var tool = new ProjectExperienceTools(store, "session", "remember", Workspace, WorkspaceService.Resolve).CreateTools()
            .Single(tool => tool.Definition.Name == ProjectExperienceTools.RememberName);
        var result = await tool.ExecuteAsync(new("remember", tool.Definition.Name, JsonSerializer.Serialize(new
        { title = "Patch review", preconditions = "Inspect current project", procedure = "Review the recorded files", evidenceSequences = new[] { sequence } }, Wire.Json)), new(Workspace));
        Assert.False(result.IsError, result.Content);
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
}
