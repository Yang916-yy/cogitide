using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Protocol;
using Workbench.Tools;
using Xunit;

namespace Workbench.Tests;

public sealed class ProjectExperienceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cogito-experience-" + Guid.NewGuid().ToString("N"));
    private string Workspace => Path.Combine(_directory, "workspace");
    private string Database => Path.Combine(_directory, "sessions.db");
    private readonly SqliteSessionStore _store;
    public ProjectExperienceTests() { Directory.CreateDirectory(Workspace); _store = new(Database); }
    private async Task<string> Session(string? workspace = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var at = DateTimeOffset.UtcNow;
        await _store.SaveAsync(new(new(id, "Fixture", workspace ?? Workspace, at, at), []));
        return id;
    }
    private ProjectExperienceTools Tools(string id, string? workspace = null) => new(_store, id, "memory-run", workspace ?? Workspace, WorkspaceService.Resolve);
    private async Task<AgentEvent> ReadEvidence(string id)
    {
        var content = "original source\n";
        await File.WriteAllTextAsync(Path.Combine(Workspace, "source.txt"), content);
        return await _store.AppendEventAsync(new(id, "source-run", "tool.completed", Call: new("read", "read_file", "{\"path\":\"source.txt\"}"),
            Result: new(JsonSerializer.Serialize(new { path = "source.txt", sha256 = Hash(content), content }, Wire.Json))));
    }
    private async Task<ToolResult> Remember(string id, long sequence, string title = "Build the C# project", string procedure = "Read source.txt before running the targeted checks.", string? replaces = null)
    {
        var args = new Dictionary<string, object?> { ["title"] = title, ["preconditions"] = "Use only for this project; check current sources.", ["procedure"] = procedure, ["evidenceSequences"] = new[] { sequence } };
        if (replaces is not null) args["supersedesId"] = replaces;
        return await Tools(id).CreateTools().Single(tool => tool.Definition.Name == ProjectExperienceTools.RememberName)
            .ExecuteAsync(new("remember", ProjectExperienceTools.RememberName, JsonSerializer.Serialize(args, Wire.Json)), new(Workspace));
    }

    [Fact]
    public async Task ProcedureSurvivesStoreRestartAndIsReusedOnlyWithinItsProject()
    {
        var first = await Session();
        var source = await ReadEvidence(first);
        var saved = await Remember(first, source.Sequence);
        Assert.False(saved.IsError, saved.Content);
        var second = await Session();
        using var reopened = new SqliteSessionStore(Database);
        var recalled = await new ProjectExperienceTools(reopened, second, "next-run", Workspace, WorkspaceService.Resolve).RecallAsync("Build C#");
        var match = Assert.Single(recalled.Matches);
        Assert.Equal("Candidate", match.Experience.Status);
        Assert.Equal(first, match.Experience.SourceSessionId);
        Assert.Equal("Intact", match.SourceStatus);
        Assert.Equal("ReportedFilesMatchOnly", match.ArtifactStatus);
        Assert.Contains("not instructions", recalled.Notice);
        Assert.Contains("does not establish all", recalled.Notice);
        var other = Path.Combine(_directory, "other");
        Directory.CreateDirectory(other);
        var third = await Session(other);
        Assert.Empty((await Tools(third, other).RecallAsync("Build C#")).Matches);
    }

    [Fact]
    public async Task ChangedFileInvalidatesApplicabilityWithoutDeletingTheOriginalCandidate()
    {
        var id = await Session();
        var source = await ReadEvidence(id);
        await Remember(id, source.Sequence);
        await File.WriteAllTextAsync(Path.Combine(Workspace, "source.txt"), "new changes");
        var match = Assert.Single((await Tools(id).RecallAsync("Build")).Matches);
        Assert.Equal("Intact", match.SourceStatus);
        Assert.Equal("ChangedOrUnavailable", match.ArtifactStatus);
        Assert.Equal("Candidate", match.Experience.Status);
    }

    [Fact]
    public async Task CommandExitZeroIsEvidenceButCannotValidateGeneralProcedure()
    {
        var id = await Session();
        var source = await _store.AppendEventAsync(new(id, "source", "tool.completed", Call: new("cmd", "run_command", "{\"command\":\"dotnet test\"}"),
            Result: new("{\"command\":\"dotnet test\",\"exitCode\":0,\"timedOut\":false,\"output\":\"passed\"}")));
        Assert.False((await Remember(id, source.Sequence)).IsError);
        var match = Assert.Single((await Tools(id).RecallAsync("Build")).Matches);
        Assert.Equal("Candidate", match.Experience.Status);
        Assert.Equal("NoArtifactCoverage", match.ArtifactStatus);
    }

    [Fact]
    public async Task FailureEvidenceCanSupportAnExplicitAvoidanceLesson()
    {
        var id = await Session();
        var source = await _store.AppendEventAsync(new(id, "source", "tool.completed", Call: new("cmd", "run_command", "{\"command\":\"bad-command\"}"), Result: new("Command not found", true)));
        var result = await Remember(id, source.Sequence, "Avoid missing build command", "Check the available toolchain before running this command.");
        Assert.False(result.IsError);
        Assert.Equal("Candidate", Assert.Single((await Tools(id).RecallAsync("Avoid")).Matches).Experience.Status);
    }

    [Fact]
    public async Task ModelClaimsAndRecursiveRecallCannotBeSavedAsOriginalEvidence()
    {
        var id = await Session();
        var claim = await _store.AppendEventAsync(new(id, "source", "message.completed", "All tests passed"));
        Assert.True((await Remember(id, claim.Sequence)).IsError);
        var recall = await _store.AppendEventAsync(new(id, "source", "tool.completed", Call: new("r", "inspect_evidence", "{}"), Result: new("All tests passed")));
        Assert.True((await Remember(id, recall.Sequence)).IsError);
        Assert.True((await Remember(id, 999)).IsError);
        Assert.DoesNotContain(await _store.EventsAsync(id), evt => evt.Type == "experience.saved");
    }

    [Fact]
    public async Task IdenticalDeltaDeduplicatesAndExplicitRevisionSupersedesOnlyItsProjectCandidate()
    {
        var id = await Session();
        var source = await ReadEvidence(id);
        var first = await Remember(id, source.Sequence);
        using var firstJson = JsonDocument.Parse(first.Content);
        var firstId = firstJson.RootElement.GetProperty("id").GetString()!;
        var duplicate = await Remember(id, source.Sequence);
        using var duplicateJson = JsonDocument.Parse(duplicate.Content);
        Assert.True(duplicateJson.RootElement.GetProperty("duplicate").GetBoolean());
        Assert.Single(await _store.EventsAsync(id), evt => evt.Type == "experience.saved");
        Assert.False((await Remember(id, source.Sequence, procedure: "Read source and check dependencies before running targeted checks.", replaces: firstId)).IsError);
        var current = Assert.Single((await Tools(id).RecallAsync("Build")).Matches).Experience;
        Assert.Equal(firstId, current.SupersedesId);
        Assert.NotEqual(firstId, current.Id);
    }

    [Fact]
    public async Task SourceTamperingIsReportedRatherThanTrustedAfterRestart()
    {
        var id = await Session();
        var source = await ReadEvidence(id);
        await Remember(id, source.Sequence);
        ReplaceEvent(source with { Result = new("different stored result") });
        var match = Assert.Single((await Tools(id).RecallAsync("Build")).Matches);
        Assert.Equal("ChangedOrUnavailable", match.SourceStatus);
        Assert.Equal("Unknown", match.ArtifactStatus);
    }

    [Fact]
    public async Task CorruptedReplacementCannotHideTheIntactOriginalCandidate()
    {
        var id = await Session();
        var originalSource = await ReadEvidence(id);
        var first = await Remember(id, originalSource.Sequence);
        using var firstJson = JsonDocument.Parse(first.Content);
        var firstId = firstJson.RootElement.GetProperty("id").GetString()!;
        var replacementSource = await ReadEvidence(id);
        Assert.False((await Remember(id, replacementSource.Sequence, procedure: "Updated build procedure.", replaces: firstId)).IsError);
        ReplaceEvent(replacementSource with { Result = new("changed source") });
        var matches = (await Tools(id).RecallAsync("Build")).Matches;
        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, match => match.Experience.Id == firstId && match.SourceStatus == "Intact");
        Assert.Contains(matches, match => match.Experience.SupersedesId == firstId && match.SourceStatus == "ChangedOrUnavailable");
    }

    [Fact]
    public async Task ChangedArtifactCannotMaskCorruptionInALaterSource()
    {
        var id = await Session();
        var first = await ReadEvidence(id);
        var second = await ReadEvidence(id);
        var args = JsonSerializer.Serialize(new { title = "Build with two sources", preconditions = "Check source files", procedure = "Check both observations", evidenceSequences = new[] { first.Sequence, second.Sequence } }, Wire.Json);
        var tool = Tools(id).CreateTools().Single(tool => tool.Definition.Name == ProjectExperienceTools.RememberName);
        Assert.False((await tool.ExecuteAsync(new("remember", tool.Definition.Name, args), new(Workspace))).IsError);
        await File.WriteAllTextAsync(Path.Combine(Workspace, "source.txt"), "changed file");
        ReplaceEvent(second with { Result = new("changed journal source") });
        var match = Assert.Single((await Tools(id).RecallAsync("Build")).Matches);
        Assert.Equal("ChangedOrUnavailable", match.SourceStatus);
        Assert.Equal("Unknown", match.ArtifactStatus);
    }

    [Theory]
    [InlineData("remember_experience")]
    [InlineData("recall_project_memory")]
    [InlineData("read_skill")]
    public async Task StoredCandidateCannotPromoteRecursiveReferenceDataToOriginalEvidence(string toolName)
    {
        var id = await Session();
        var source = await ReadEvidence(id);
        await Remember(id, source.Sequence);
        var recursive = await _store.AppendEventAsync(new(id, "source", "tool.completed", Call: new("data", toolName, "{}"), Result: new("reference data")));
        var saved = Assert.Single(await _store.EventsAsync(id), evt => evt.Type == "experience.saved");
        var candidate = JsonSerializer.Deserialize<ProjectExperience>(saved.Text!, Wire.Json)!;
        candidate = candidate with { Sources = [new(recursive.Sequence, EvidenceMemory.EventSha256(recursive), toolName, Hash(recursive.Result!.Content), null)] };
        ReplaceEvent(saved with { Text = JsonSerializer.Serialize(candidate, Wire.Json) });
        var recalled = Assert.Single((await Tools(id).RecallAsync("Build")).Matches);
        Assert.Equal("ChangedOrUnavailable", recalled.SourceStatus);
    }

    [Theory]
    [InlineData("self")]
    [InlineData("malformed")]
    [InlineData("prefix")]
    public async Task MalformedStoredCandidateMetadataIsExcluded(string change)
    {
        var id = await Session();
        var source = await ReadEvidence(id);
        await Remember(id, source.Sequence);
        var saved = Assert.Single(await _store.EventsAsync(id), evt => evt.Type == "experience.saved");
        var candidate = JsonSerializer.Deserialize<ProjectExperience>(saved.Text!, Wire.Json)!;
        candidate = change switch
        {
            "self" => candidate with { SupersedesId = candidate.Id },
            "malformed" => candidate with { SupersedesId = "not-an-id" },
            _ => candidate with { PathPrefix = "../outside" }
        };
        if (change == "prefix") candidate = candidate with { Id = Hash(JsonSerializer.Serialize(new { title = candidate.Title, preconditions = candidate.Preconditions, procedure = candidate.Procedure, prefix = candidate.PathPrefix }, Wire.Json)) };
        ReplaceEvent(saved with { Text = JsonSerializer.Serialize(candidate, Wire.Json) });
        Assert.Empty((await Tools(id).RecallAsync("Build")).Matches);
    }

    [Fact]
    public async Task InvalidInputAndCancelledWritesDoNotCreateMemory()
    {
        var id = await Session();
        var memory = Tools(id).CreateTools().Single(tool => tool.Definition.Name == ProjectExperienceTools.RememberName);
        string[] invalid = ["{}", "{\"title\":\"x\",\"preconditions\":\"y\",\"procedure\":\"z\",\"evidenceSequences\":[]}", "{\"sessionId\":\"other\"}"];
        foreach (var json in invalid) Assert.True((await memory.ExecuteAsync(new("m", memory.Definition.Name, json), new(Workspace))).IsError);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => memory.ExecuteAsync(new("m", memory.Definition.Name, "{}"), new(Workspace), cancelled.Token));
        Assert.DoesNotContain(await _store.EventsAsync(id), evt => evt.Type == "experience.saved");
    }

    [Fact]
    public async Task RecallIsBoundedAndKeepsWholeRecords()
    {
        var id = await Session();
        var source = await ReadEvidence(id);
        for (var i = 0; i < 8; i++) Assert.False((await Remember(id, source.Sequence, "Build case " + i, "Procedure " + new string('中', 1000))).IsError);
        var result = await Tools(id).CreateTools().Single(tool => tool.Definition.Name == ProjectExperienceTools.RecallName)
            .ExecuteAsync(new("recall", ProjectExperienceTools.RecallName, "{\"query\":\"Build\",\"limit\":8}"), new(Workspace));
        Assert.False(result.IsError);
        Assert.True(result.Content.Length <= 16_000);
        using var document = JsonDocument.Parse(result.Content);
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.InRange(document.RootElement.GetProperty("matches").GetArrayLength(), 1, 7);
    }

    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private void ReplaceEvent(AgentEvent evt)
    {
        using var connection = new SqliteConnection("Data Source=" + Database);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE events SET payload=$payload WHERE session_id=$id AND sequence=$sequence";
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(evt, Wire.Json));
        command.Parameters.AddWithValue("$id", evt.SessionId);
        command.Parameters.AddWithValue("$sequence", evt.Sequence);
        command.ExecuteNonQuery();
    }
    public void Dispose() { _store.Dispose(); SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
}
