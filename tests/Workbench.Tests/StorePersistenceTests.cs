using Microsoft.Data.Sqlite;
using Workbench.Core;
using Workbench.Protocol;
using Xunit;

namespace Workbench.Tests;

public sealed class StorePersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "workbench-store-" + Guid.NewGuid().ToString("N"));
    public StorePersistenceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ReopenPreservesDataAndRecoveryDoesNotReplayTools()
    {
        var path = Path.Combine(_directory, "data.db");
        var now = DateTimeOffset.UtcNow;
        var session = new AgentSession(new("session", "中文会话", _directory, now, now, "running"),
            [new("user", "task"), new("assistant", "", [new("uncertain-call", "write_file", "{}")])]);
        using (var store = new SqliteSessionStore(path))
        {
            await store.SaveAsync(session);
            await store.AppendEventAsync(new("session", "run", "tool.started", Call: new("uncertain-call", "write_file", "{}")));
        }
        using var reopened = new SqliteSessionStore(path);
        Assert.Equal("running", (await reopened.LoadAsync("session"))!.Info.Status);
        Assert.Equal(1, await reopened.RecoverInterruptedAsync());
        Assert.Equal(0, await reopened.RecoverInterruptedAsync());
        var result = (await reopened.LoadAsync("session"))!;
        Assert.Equal("interrupted", result.Info.Status);
        Assert.Equal("中文会话", result.Info.Title);
        Assert.Equal(2, result.Messages.Count);
        Assert.Single(await reopened.EventsAsync("session"));
    }

    [Fact]
    public async Task ConcurrentAppendsHaveUniqueMonotonicPerSessionSequences()
    {
        using var store = new SqliteSessionStore(Path.Combine(_directory, "data.db"));
        var now = DateTimeOffset.UtcNow;
        await store.SaveAsync(new(new("a", "A", _directory, now, now), []));
        await store.SaveAsync(new(new("b", "B", _directory, now, now), []));
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => store.AppendEventAsync(new("a", "run", "sample", i.ToString()))));
        await store.AppendEventAsync(new("b", "run", "sample"));
        var events = await store.EventsAsync("a", 16);
        Assert.Equal(Enumerable.Range(17, 16).Select(x => (long)x), events.Select(e => e.Sequence));
        Assert.Equal(1, (await store.EventsAsync("b"))[0].Sequence);
        Assert.Equal(events[^1], await store.LastEventAsync("a"));
        Assert.Equal("b", (await store.LastEventAsync("b"))!.SessionId);
        Assert.Null(await store.LastEventAsync("missing"));
    }

    [Fact]
    public void FutureSchemaIsRejectedWithoutResettingDatabase()
    {
        var path = Path.Combine(_directory, "future.db");
        using (var connection = new SqliteConnection("Data Source=" + path))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=99; CREATE TABLE important_data(value TEXT); INSERT INTO important_data VALUES('preserve');";
            command.ExecuteNonQuery();
        }
        Assert.Throws<InvalidOperationException>(() => new SqliteSessionStore(path));
        using var check = new SqliteConnection("Data Source=" + path);
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT value FROM important_data";
        Assert.Equal("preserve", query.ExecuteScalar());
    }

    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(_directory, true); }
}
