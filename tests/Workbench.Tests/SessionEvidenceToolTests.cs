using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class SessionEvidenceToolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "cogito-recall-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "sessions.db");
    public SessionEvidenceToolTests() => Directory.CreateDirectory(_directory);

    private async Task<SqliteSessionStore> Store(string status = "running")
    {
        var store = new SqliteSessionStore(Database);
        await store.SaveAsync(new(new("session", "Task", _directory, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, status), []));
        return store;
    }

    private static Task<AgentEvent> Record(ISessionStore store, string? content = null, string? arguments = null) =>
        store.AppendEventAsync(new("session", "run", "tool.completed", Call: new("read-1", "read_file", arguments ?? "{\"path\":\"a.cs\"}"),
            Result: new(content ?? JsonSerializer.Serialize(new { path = "a.cs", sha256 = new string('b', 64), content = "fixture" }))));

    private Task<ToolResult> Execute(SessionEvidenceTool tool, string arguments = "{}", CancellationToken ct = default) =>
        tool.ExecuteAsync(new("recall", SessionEvidenceTool.Name, arguments), new(_directory), ct);

    private static JsonElement Payload(ToolResult result) => JsonDocument.Parse(result.Content[(result.Content.IndexOf('\n') + 1)..]).RootElement.Clone();

    [Fact]
    public async Task RecallSurvivesReopenAndUsesExactStoredSourceHashes()
    {
        AgentEvent stored;
        using (var store = await Store()) stored = await Record(store);
        using var reopened = new SqliteSessionStore(Database);
        var tool = new SessionEvidenceTool(reopened, "session", _directory);
        var summary = await Execute(tool);
        Assert.False(summary.IsError);
        Assert.Equal("read_file", Payload(summary).GetProperty("entries")[0].GetProperty("toolName").GetString());
        var detail = await Execute(tool, $"{{\"eventSequence\":{stored.Sequence}}}");
        Assert.False(detail.IsError);
        var payload = Payload(detail);
        var source = payload.GetProperty("source");
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(stored, Wire.Json))), source.GetProperty("eventSha256").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stored.Result!.Content))), source.GetProperty("resultSha256").GetString());
        Assert.Equal(stored.Result.Content, payload.GetProperty("resultExcerpt").GetString());
        Assert.Equal((await tool.ReadSnapshotAsync()).Entries[0].Source.EventSha256, source.GetProperty("eventSha256").GetString());
    }

    [Theory]
    [InlineData(SessionEvidenceTool.Name)]
    [InlineData(ProjectExperienceTools.RecallName)]
    [InlineData(ProjectExperienceTools.RememberName)]
    public async Task MemoryOperationsCannotBecomeRecursiveEvidence(string name)
    {
        using var store = await Store();
        await Record(store);
        var memory = await store.AppendEventAsync(new("session", "run", "tool.completed", Call: new("memory", name, "{}"),
            Result: new("A historical procedure was recalled or saved.")));
        var tool = new SessionEvidenceTool(store, "session", _directory);
        Assert.Equal("read_file", Assert.Single((await tool.ReadSnapshotAsync()).Entries).ToolName);
        Assert.True((await Execute(tool, $"{{\"eventSequence\":{memory.Sequence}}}")).IsError);
    }

    [Theory]
    [InlineData("{\"sessionId\":\"another\"}")]
    [InlineData("{\"eventSequence\":0}")]
    [InlineData("{\"eventSequence\":1.5}")]
    [InlineData("{\"eventSequence\":1,\"eventSequence\":2}")]
    [InlineData("{\"eventSequence\":\"1\"}")]
    [InlineData("[]")]
    public async Task RequestCannotChangeScopeOrSmuggleExtraArguments(string arguments)
    {
        using var store = await Store();
        await Record(store);
        Assert.True((await Execute(new(store, "session", _directory), arguments)).IsError);
    }

    [Fact]
    public async Task OtherSessionAndWorkspaceDataCannotBeRead()
    {
        using var store = await Store();
        await store.SaveAsync(new(new("another", "Other", _directory, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), []));
        await store.AppendEventAsync(new("another", "run", "tool.completed", Call: new("secret", "read_file", "{}"), Result: new("other-session-marker")));
        var tool = new SessionEvidenceTool(store, "session", _directory);
        var result = await Execute(tool, "{\"eventSequence\":1}");
        Assert.True(result.IsError);
        Assert.DoesNotContain("other-session-marker", result.Content);
        Assert.True((await tool.ExecuteAsync(new("id", SessionEvidenceTool.Name, "{}"), new(Path.GetTempPath()))).IsError);
        Assert.True((await Execute(new(store, "session", Path.GetTempPath()))).IsError);
    }

    [Fact]
    public async Task ModelClaimsAndRecursiveRecallAreExcludedFromLedgerAndDetail()
    {
        using var store = await Store();
        var original = await Record(store);
        var claim = await store.AppendEventAsync(new("session", "run", "message.completed", "All tests passed", Sequence: 0));
        var nested = await store.AppendEventAsync(new("session", "run", "tool.completed", Call: new("recall", SessionEvidenceTool.Name, "{}"), Result: new("Nested old observations")));
        var tool = new SessionEvidenceTool(store, "session", _directory);
        var snapshot = await tool.ReadSnapshotAsync();
        Assert.Equal(original.Sequence, Assert.Single(snapshot.Entries).Source.EventSequence);
        Assert.Equal(nested.Sequence, snapshot.ThroughSequence);
        Assert.True((await Execute(tool, $"{{\"eventSequence\":{claim.Sequence}}}")).IsError);
        Assert.True((await Execute(tool, $"{{\"eventSequence\":{nested.Sequence}}}")).IsError);
    }

    [Fact]
    public async Task IdleFreshnessIsDerivedWithoutForgingJournalEventsOrChangingDigest()
    {
        using var store = await Store();
        await Record(store);
        var tool = new SessionEvidenceTool(store, "session", _directory);
        var running = await tool.ReadSnapshotAsync();
        var session = (await store.LoadAsync("session"))!;
        await store.SaveAsync(session with { Info = session.Info with { Status = "completed" } });
        var idle = await tool.ReadSnapshotAsync();
        Assert.Equal(EvidenceValidity.Current, Assert.Single(running.Entries).Validity);
        Assert.Equal(EvidenceValidity.Stale, Assert.Single(idle.Entries).Validity);
        Assert.NotNull(idle.FreshnessNote);
        Assert.Equal(running.SourceDigest, idle.SourceDigest);
        Assert.Single(await store.EventsAsync("session"));
        Assert.Equal("Stale", Payload(await Execute(tool)).GetProperty("entries")[0].GetProperty("validity").GetString());
    }

    [Fact]
    public async Task CustomMutatingToolsInvalidateOriginalEvidence()
    {
        using var store = await Store();
        await Record(store);
        await store.AppendEventAsync(new("session", "run", "tool.started", Call: new("custom", "custom_writer", "{}")));
        var tool = new SessionEvidenceTool(store, "session", _directory, new HashSet<string> { "custom_writer" });
        Assert.Equal(EvidenceValidity.Stale, Assert.Single((await tool.ReadSnapshotAsync()).Entries).Validity);
    }

    [Fact]
    public async Task LargeEscapedDetailIsBoundedAndHasFullStringHashes()
    {
        using var store = await Store();
        var content = new string('\0', 20_000);
        var args = JsonSerializer.Serialize(new { path = new string('\u4e2d', 8000) });
        var stored = await Record(store, content, args);
        var tool = new SessionEvidenceTool(store, "session", _directory);
        var detail = await Execute(tool, $"{{\"eventSequence\":{stored.Sequence}}}");
        Assert.False(detail.IsError);
        Assert.InRange(detail.Content.Length, 1, 16_000);
        var payload = Payload(detail);
        Assert.True(payload.GetProperty("argumentsTruncated").GetBoolean());
        Assert.True(payload.GetProperty("resultTruncated").GetBoolean());
        Assert.Equal(content.Length, payload.GetProperty("resultCharacters").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))), payload.GetProperty("source").GetProperty("resultSha256").GetString());
    }

    [Fact]
    public async Task CancellationDoesNotTurnIntoAnEvidenceResult()
    {
        using var store = await Store();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Execute(new(store, "session", _directory), ct: cancellation.Token));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }
}
