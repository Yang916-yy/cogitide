using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class SessionRecoveryCursorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cogitide-recovery-cursor-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "sessions.db");
    public SessionRecoveryCursorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task MessageSaveCapturesJournalBoundaryWhileUsageOnlySavePreservesUnfinishedDeltas()
    {
        using var store = new SqliteSessionStore(Database);
        var initial = Session();
        await store.SaveAsync(initial);
        var first = await store.AppendEventAsync(new("session", "run", "message.delta", "Hello"));
        var usage = initial with { RunCheckpoint = initial.RunCheckpoint! with { Usage = new(10, 1, 1) } };
        await store.SaveAsync(usage);
        var streaming = (await store.ReadViewAsync("session"))!;
        Assert.Equal(0, streaming.MessageEventSequence);
        Assert.Single(streaming.Messages);
        Assert.Equal(first, Assert.Single((await store.EventsSincePageAsync("session", streaming.MessageEventSequence!.Value)).Items));
        var second = await store.AppendEventAsync(new("session", "run", "message.delta", " world"));
        var committed = usage with { Messages = [.. usage.Messages, new("assistant", "Hello world")] };
        await store.SaveAsync(committed);
        var completed = await store.AppendEventAsync(new("session", "run", "message.completed", "Hello world"));
        await store.SaveAsync(committed with { Info = committed.Info with { Status = "completed" } });
        var view = (await store.ReadViewAsync("session"))!;
        Assert.Equal(second.Sequence, view.MessageEventSequence);
        Assert.Equal("Hello world", view.Messages[^1].Content);
        Assert.Equal(completed, Assert.Single((await store.EventsSincePageAsync("session", view.MessageEventSequence!.Value)).Items));
    }

    [Fact]
    public async Task ForwardRecoveryPagesStayBoundedAndFiniteWhenTheSourceKeepsAppending()
    {
        using var store = new SqliteSessionStore(Database);
        await store.SaveAsync(Session());
        for (var index = 0; index < 613; index++) await store.AppendEventAsync(new("session", "run", "message.delta", index.ToString()));
        var first = await store.EventsSincePageAsync("session", 0, limit: 37);
        Assert.Equal(613, first.ThroughSequence);
        Assert.Equal(37, first.Items.Count);
        Assert.True(first.HasMore);
        await store.AppendEventAsync(new("session", "run", "message.delta", "after captured boundary"));
        List<long> ids = [.. first.Items.Select(e => e.Sequence)];
        var cursor = first.NextAfterSequence;
        var pages = 1;
        while (true)
        {
            var page = await store.EventsSincePageAsync("session", cursor, first.ThroughSequence, limit: 37);
            Assert.Equal(first.ThroughSequence, page.ThroughSequence);
            Assert.InRange(page.Items.Count, 1, 37);
            Assert.True(page.NextAfterSequence > cursor);
            ids.AddRange(page.Items.Select(e => e.Sequence));
            cursor = page.NextAfterSequence;
            pages++;
            if (!page.HasMore) break;
        }
        Assert.Equal(17, pages);
        Assert.Equal(Enumerable.Range(1, 613).Select(i => (long)i), ids);
        var newer = await store.EventsSincePageAsync("session", cursor);
        Assert.Equal(614, Assert.Single(newer.Items).Sequence);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.EventsSincePageAsync("session", -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.EventsSincePageAsync("session", 1, 0));
    }

    [Fact]
    public async Task OversizedIndivisibleEventNeverSilentlyAdvancesReplayCursor()
    {
        using var store = new SqliteSessionStore(Database);
        await store.SaveAsync(Session());
        await store.AppendEventAsync(new("session", "run", "tool.output", new string('x', SessionHistoryPages.MaximumPageCharacters + 100)));
        var page = await store.EventsSincePageAsync("session", 0);
        Assert.False(page.Available);
        Assert.True(page.HasMore);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.NextAfterSequence);
        Assert.Equal(1, page.ThroughSequence);
    }

    [Fact]
    public async Task VersionOneMigrationPreservesUnknownLegacyBoundaryUntilMessagesActuallyChange()
    {
        var original = Session();
        using (var connection = new SqliteConnection("Data Source=" + Database))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE sessions (id TEXT PRIMARY KEY,payload TEXT NOT NULL,updated_at TEXT NOT NULL);
                CREATE TABLE events (session_id TEXT NOT NULL,sequence INTEGER NOT NULL,payload TEXT NOT NULL,PRIMARY KEY(session_id,sequence));
                INSERT INTO sessions VALUES('session',$payload,$now);
                PRAGMA user_version=1;
                """;
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(original, Wire.Json));
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        using var store = new SqliteSessionStore(Database);
        Assert.Null((await store.ReadViewAsync("session"))!.MessageEventSequence);
        await store.AppendEventAsync(new("session", "run", "message.delta", "unconfirmed legacy stream"));
        await store.SaveAsync(original with { Info = original.Info with { Status = "interrupted" } });
        Assert.Null((await store.ReadViewAsync("session"))!.MessageEventSequence);
        await store.SaveAsync(original with { Messages = [.. original.Messages, new("assistant", "New committed message")] });
        Assert.Equal(1, (await store.ReadViewAsync("session"))!.MessageEventSequence);
    }

    internal static AgentSession Session()
    {
        var now = DateTimeOffset.UtcNow;
        return new(new("session", "Projection fixture", Path.GetTempPath(), now, now, "running"), [new("user", "Original task")],
            RunCheckpoint: new(1, "run", 0, new(), 0, now));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }
}
