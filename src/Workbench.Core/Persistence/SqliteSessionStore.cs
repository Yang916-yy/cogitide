using System.Text.Json;
using Microsoft.Data.Sqlite;
using Workbench.Protocol;

namespace Workbench.Core;

/// <summary>Durable session snapshots and an append-only, per-session event journal.</summary>
public sealed class SqliteSessionStore : ISessionStore, IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteSessionStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true, DefaultTimeout = 15
        }.ToString();
        using var connection = Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        var found = Convert.ToInt32(version.ExecuteScalar());
        if (found > SchemaVersion)
            throw new InvalidOperationException($"Database schema {found} is newer than supported schema {SchemaVersion}. No data was changed.");
        if (found == 0)
        {
            using var transaction = connection.BeginTransaction();
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = """
                CREATE TABLE IF NOT EXISTS sessions (id TEXT PRIMARY KEY, payload TEXT NOT NULL, updated_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS events (session_id TEXT NOT NULL, sequence INTEGER NOT NULL, payload TEXT NOT NULL,
                    PRIMARY KEY(session_id, sequence), FOREIGN KEY(session_id) REFERENCES sessions(id));
                PRAGMA user_version = 1;
                """;
            migration.ExecuteNonQuery();
            transaction.Commit();
        }
        using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode=WAL;";
        mode.ExecuteScalar();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;";
        command.ExecuteNonQuery();
        return connection;
    }

    public async Task<IReadOnlyList<SessionInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM sessions ORDER BY updated_at DESC, id;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            List<SessionInfo> result = [];
            while (await reader.ReadAsync(cancellationToken)) result.Add(Deserialize(reader.GetString(0)).Info);
            return result;
        }
        finally { _gate.Release(); }
    }

    public async Task<AgentSession?> LoadAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM sessions WHERE id=$id;";
            command.Parameters.AddWithValue("$id", id);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is string json ? Deserialize(json) : null;
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AgentSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Save(connection, transaction, session);
            transaction.Commit();
        }
        finally { _gate.Release(); }
    }

    private static void Save(SqliteConnection connection, SqliteTransaction transaction, AgentSession session)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sessions(id,payload,updated_at) VALUES($id,$payload,$updated)
            ON CONFLICT(id) DO UPDATE SET payload=excluded.payload, updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", session.Info.Id);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(session, Wire.Json));
        command.Parameters.AddWithValue("$updated", session.Info.UpdatedAt.ToUniversalTime().ToString("O"));
        command.ExecuteNonQuery();
    }

    public async Task<AgentEvent> AppendEventAsync(AgentEvent evt, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var sequence = connection.CreateCommand();
            sequence.Transaction = transaction;
            sequence.CommandText = "SELECT COALESCE(MAX(sequence),0)+1 FROM events WHERE session_id=$id;";
            sequence.Parameters.AddWithValue("$id", evt.SessionId);
            var stored = evt with { Sequence = Convert.ToInt64(sequence.ExecuteScalar()), Timestamp = evt.Timestamp ?? DateTimeOffset.UtcNow };
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO events(session_id,sequence,payload) VALUES($id,$sequence,$payload);";
            command.Parameters.AddWithValue("$id", stored.SessionId);
            command.Parameters.AddWithValue("$sequence", stored.Sequence);
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(stored, Wire.Json));
            await command.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
            return stored;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<AgentEvent>> EventsAsync(string sessionId, long afterSequence = 0, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM events WHERE session_id=$id AND sequence>$after ORDER BY sequence;";
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$after", afterSequence);
            List<AgentEvent> result = [];
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(JsonSerializer.Deserialize<AgentEvent>(reader.GetString(0), Wire.Json) ?? throw new InvalidDataException("Invalid stored event."));
            return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Call once while the Host owns the data directory, before accepting runs. Never replays tools.</summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            List<AgentSession> interrupted = [];
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT payload FROM sessions;";
                using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var session = Deserialize(reader.GetString(0));
                    if (session.Info.Status is "running" or "waiting_approval") interrupted.Add(session);
                }
            }
            foreach (var session in interrupted)
                Save(connection, transaction, session with { Info = session.Info with { Status = "interrupted", UpdatedAt = DateTimeOffset.UtcNow } });
            transaction.Commit();
            return interrupted.Count;
        }
        finally { _gate.Release(); }
    }

    private static AgentSession Deserialize(string json) => JsonSerializer.Deserialize<AgentSession>(json, Wire.Json) ?? throw new InvalidDataException("Invalid stored session; the database was preserved.");
    public void Dispose() => _gate.Dispose();
}
