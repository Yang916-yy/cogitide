using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Workbench.Protocol;

namespace Workbench.Core;

/// <summary>Durable session snapshots and an append-only, per-session event journal.</summary>
public sealed class SqliteSessionStore : ISessionStore, IDisposable
{
    // Display-only projection: source snapshots and model LoadAsync retain image bytes and opaque native state.
    private const string DisplayMessageSql = """
        json_set(json_remove(j.value,'$.continuation'),'$.images',json(CASE WHEN json_type(j.value,'$.images')='array'
            THEN (SELECT json_group_array(json_set(img.value,'$.base64Data','')) FROM json_each(j.value,'$.images') img)
            ELSE 'null' END))
        """;
    private const int SchemaVersion = 2;
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
        if (found < SchemaVersion)
        {
            using var transaction = connection.BeginTransaction();
            // Another store instance may have migrated while this connection waited for SQLite's
            // write transaction. Re-read under that transaction instead of racing duplicate ALTERs.
            using var current = connection.CreateCommand();
            current.Transaction = transaction;
            current.CommandText = "PRAGMA user_version;";
            found = Convert.ToInt32(current.ExecuteScalar());
            if (found > SchemaVersion) throw new InvalidOperationException($"Database schema {found} is newer than supported schema {SchemaVersion}. No data was changed.");
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            if (found == 0)
            {
                migration.CommandText = """
                    CREATE TABLE IF NOT EXISTS sessions (id TEXT PRIMARY KEY, payload TEXT NOT NULL, updated_at TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS events (session_id TEXT NOT NULL, sequence INTEGER NOT NULL, payload TEXT NOT NULL,
                        PRIMARY KEY(session_id, sequence), FOREIGN KEY(session_id) REFERENCES sessions(id));
                    """;
                migration.ExecuteNonQuery();
            }
            if (found < 2)
            {
                migration.CommandText = "ALTER TABLE sessions ADD COLUMN message_event_sequence INTEGER NULL; PRAGMA user_version=2;";
                migration.ExecuteNonQuery();
            }
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
            command.CommandText = "SELECT json_extract(payload,'$.info') FROM sessions ORDER BY updated_at DESC, id;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            List<SessionInfo> result = [];
            while (await reader.ReadAsync(cancellationToken)) result.Add(JsonSerializer.Deserialize<SessionInfo>(reader.GetString(0), Wire.Json)
                ?? throw new InvalidDataException("Invalid stored session metadata."));
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
            INSERT INTO sessions(id,payload,updated_at,message_event_sequence)
                VALUES($id,$payload,$updated,(SELECT COALESCE(MAX(sequence),0) FROM events WHERE session_id=$id))
            ON CONFLICT(id) DO UPDATE SET payload=excluded.payload, updated_at=excluded.updated_at,
                message_event_sequence=CASE
                    WHEN json_extract(sessions.payload,'$.messages') IS NOT json_extract(excluded.payload,'$.messages')
                    THEN excluded.message_event_sequence ELSE sessions.message_event_sequence END;
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

    /// <summary>Read one indexed journal tail record without materializing a long conversation.</summary>
    public async Task<AgentEvent?> LastEventAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM events WHERE session_id=$id ORDER BY sequence DESC LIMIT 1;";
            command.Parameters.AddWithValue("$id", sessionId);
            var payload = await command.ExecuteScalarAsync(cancellationToken);
            return payload is string json ? JsonSerializer.Deserialize<AgentEvent>(json, Wire.Json) : null;
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

    public async Task<AgentEvent?> GetEventAsync(string sessionId, long sequence, CancellationToken cancellationToken = default)
    {
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT payload FROM events WHERE session_id=$id AND sequence=$sequence;";
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$sequence", sequence);
            return await command.ExecuteScalarAsync(cancellationToken) is string json
                ? JsonSerializer.Deserialize<AgentEvent>(json, Wire.Json) : null;
        }
        finally { _gate.Release(); }
    }

    public async IAsyncEnumerable<AgentEvent> StreamEventsAsync(string sessionId, long afterSequence = 0, bool descending = false,
        string? eventType = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        long upper;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var maximum = connection.CreateCommand();
            maximum.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM events WHERE session_id=$id;";
            maximum.Parameters.AddWithValue("$id", sessionId);
            upper = Convert.ToInt64(await maximum.ExecuteScalarAsync(cancellationToken));
        }
        finally { _gate.Release(); }
        long? cursor = null;
        while (true)
        {
            List<AgentEvent> batch = [];
            await _gate.WaitAsync(cancellationToken);
            try
            {
                using var connection = Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT payload,length(payload) FROM events WHERE session_id=$id AND sequence>$after AND sequence<=$upper
                        AND ($cursor IS NULL OR sequence{(descending ? "<" : ">")}$cursor)
                        AND ($type IS NULL OR json_extract(payload,'$.type')=$type)
                    ORDER BY sequence {(descending ? "DESC" : "ASC")} LIMIT 100;
                    """;
                command.Parameters.AddWithValue("$id", sessionId);
                command.Parameters.AddWithValue("$after", afterSequence);
                command.Parameters.AddWithValue("$upper", upper);
                command.Parameters.AddWithValue("$cursor", cursor is { } position ? position : DBNull.Value);
                command.Parameters.AddWithValue("$type", eventType is null ? DBNull.Value : eventType);
                long characters = 0;
                using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var length = reader.GetInt64(1);
                    if (batch.Count > 0 && characters + length > SessionHistoryPages.MaximumPageCharacters) break;
                    // A source event is indivisible. Streaming may expose one oversized original, never silently omit it.
                    batch.Add(JsonSerializer.Deserialize<AgentEvent>(reader.GetString(0), Wire.Json)
                        ?? throw new InvalidDataException("Invalid stored event."));
                    characters += length;
                }
            }
            finally { _gate.Release(); }
            if (batch.Count == 0) yield break;
            cursor = batch[^1].Sequence;
            // No database reader, transaction or store gate remains owned while the consumer processes an event.
            foreach (var evt in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return evt;
            }
        }
    }

    public async Task<SessionEventPage> EventsPageAsync(string sessionId, long? beforeSequence = null, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        SessionHistoryPages.ValidateLimit(limit);
        if (beforeSequence is < 1) throw new ArgumentOutOfRangeException(nameof(beforeSequence));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT length(payload),payload FROM events WHERE session_id=$id AND ($before IS NULL OR sequence<$before) ORDER BY sequence DESC LIMIT $limit;";
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$before", beforeSequence is { } before ? before : DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit + 1);
            List<AgentEvent> result = [];
            long size = 2;
            var hasMore = false;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var length = reader.GetInt64(0) + 1;
                if (result.Count == limit || size + length > SessionHistoryPages.MaximumPageCharacters)
                {
                    hasMore = true;
                    break;
                }
                size += length;
                result.Add(JsonSerializer.Deserialize<AgentEvent>(reader.GetString(1), Wire.Json)
                    ?? throw new InvalidDataException("Invalid stored event."));
            }
            if (result.Count == 0 && hasMore)
                return new([], beforeSequence, true, false, "The next event exceeds the history page character budget. Use its exact source event reference.");
            result.Reverse();
            return new(result, result.Count > 0 ? result[0].Sequence : beforeSequence, hasMore);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Bounded forward replay against one captured source boundary. Does not execute journal content.</summary>
    public async Task<SessionEventReplayPage> EventsSincePageAsync(string sessionId, long afterSequence, long? throughSequence = null,
        int limit = 100, CancellationToken cancellationToken = default)
    {
        SessionHistoryPages.ValidateLimit(limit);
        if (afterSequence < 0) throw new ArgumentOutOfRangeException(nameof(afterSequence));
        if (throughSequence is { } through && (through < 0 || through < afterSequence)) throw new ArgumentOutOfRangeException(nameof(throughSequence));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var maximum = connection.CreateCommand();
            maximum.Transaction = transaction;
            maximum.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM events WHERE session_id=$id;";
            maximum.Parameters.AddWithValue("$id", sessionId);
            var currentMaximum = Convert.ToInt64(await maximum.ExecuteScalarAsync(cancellationToken));
            var upper = throughSequence ?? currentMaximum;
            if (afterSequence > currentMaximum || upper > currentMaximum) throw new ArgumentOutOfRangeException(nameof(throughSequence), "The replay cursor exceeds the stored journal boundary.");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT sequence,length(payload),payload FROM events
                WHERE session_id=$id AND sequence>$after AND sequence<=$upper ORDER BY sequence LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$id", sessionId);
            command.Parameters.AddWithValue("$after", afterSequence);
            command.Parameters.AddWithValue("$upper", upper);
            command.Parameters.AddWithValue("$limit", limit + 1);
            List<AgentEvent> result = [];
            long characters = 2;
            var hasMore = false;
            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                while (await reader.ReadAsync(cancellationToken))
                {
                    var length = reader.GetInt64(1) + 1;
                    if (result.Count == limit || characters + length > SessionHistoryPages.MaximumPageCharacters) { hasMore = true; break; }
                    result.Add(JsonSerializer.Deserialize<AgentEvent>(reader.GetString(2), Wire.Json) ?? throw new InvalidDataException("Invalid stored event."));
                    characters += length;
                }
            transaction.Commit();
            if (result.Count == 0 && hasMore)
                return new([], afterSequence, upper, true, false, "The next source event exceeds the recovery page character budget; its cursor was not advanced.");
            return new(result, result.Count > 0 ? result[^1].Sequence : afterSequence, upper, hasMore);
        }
        finally { _gate.Release(); }
    }

    public async Task<SessionView?> ReadViewAsync(string id, int messageLimit = 100, CancellationToken cancellationToken = default)
    {
        SessionHistoryPages.ValidateLimit(messageLimit);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var metadata = connection.CreateCommand();
            metadata.Transaction = transaction;
            metadata.CommandText = "SELECT json_extract(payload,'$.info'),json_extract(payload,'$.compaction'),json_extract(payload,'$.runCheckpoint'),json_array_length(payload,'$.messages'),message_event_sequence FROM sessions WHERE id=$id;";
            metadata.Parameters.AddWithValue("$id", id);
            SessionInfo info;
            CompactionCheckpoint? compaction;
            RunCheckpoint? checkpoint;
            int count;
            long? messageEventSequence;
            using (var reader = await metadata.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken)) return null;
                info = JsonSerializer.Deserialize<SessionInfo>(reader.GetString(0), Wire.Json) ?? throw new InvalidDataException("Invalid stored session metadata.");
                compaction = reader.IsDBNull(1) ? null : JsonSerializer.Deserialize<CompactionCheckpoint>(reader.GetString(1), Wire.Json);
                checkpoint = reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<RunCheckpoint>(reader.GetString(2), Wire.Json);
                count = reader.GetInt32(3);
                messageEventSequence = reader.IsDBNull(4) ? null : reader.GetInt64(4);
            }
            var page = await ReadMessagesAsync(connection, transaction, id, count, null, messageLimit, cancellationToken);
            var pending = await ReadPendingInputsAsync(connection, transaction, id, 128, cancellationToken);
            transaction.Commit();
            return new(info, page.Messages, compaction, count, page.FirstMessageIndex, page.HasEarlierMessages,
                checkpoint, page.Available, page.UnavailableReason, pending, page.OmittedPayloads, MessageEventSequence: messageEventSequence);
        }
        finally { _gate.Release(); }
    }

    public async Task<SessionMessagePage?> MessagesPageAsync(string id, int? beforeIndex = null, int limit = 100,
        CancellationToken cancellationToken = default)
    {
        SessionHistoryPages.ValidateLimit(limit);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT json_array_length(payload,'$.messages') FROM sessions WHERE id=$id;";
            count.Parameters.AddWithValue("$id", id);
            if (await count.ExecuteScalarAsync(cancellationToken) is not long total) return null;
            var page = await ReadMessagesAsync(connection, transaction, id, checked((int)total), beforeIndex, limit, cancellationToken);
            transaction.Commit();
            return page;
        }
        finally { _gate.Release(); }
    }

    private static async Task<SessionMessagePage> ReadMessagesAsync(SqliteConnection connection, SqliteTransaction transaction,
        string id, int total, int? beforeIndex, int limit, CancellationToken token)
    {
        var end = beforeIndex ?? total;
        if (end < 0 || end > total) throw new ArgumentOutOfRangeException(nameof(beforeIndex));
        using var boundary = connection.CreateCommand();
        boundary.Transaction = transaction;
        boundary.CommandText = "SELECT json_extract(payload,'$.messages[' || $end || '].role') FROM sessions WHERE id=$id;";
        boundary.Parameters.AddWithValue("$id", id);
        boundary.Parameters.AddWithValue("$end", end);
        if (end < total && await boundary.ExecuteScalarAsync(token) is string role && role == "tool")
            throw new ArgumentException("The cursor splits a tool group.", nameof(beforeIndex));
        // SQLite parses the snapshot; only bounded whole groups cross into managed memory. A tool group is indivisible.
        using var groups = connection.CreateCommand();
        groups.Transaction = transaction;
        groups.CommandText = $"""
            WITH raw AS (
                SELECT CAST(j.key AS INTEGER) AS i,{DisplayMessageSql} AS value,json_extract(j.value,'$.role') AS role
                FROM sessions s,json_each(s.payload,'$.messages') j WHERE s.id=$id AND CAST(j.key AS INTEGER)<$end
            ), grouped AS (
                SELECT i,length(value)+1 AS size,MAX(CASE WHEN role!='tool' THEN i END) OVER(ORDER BY i) AS first FROM raw
            ) SELECT COALESCE(first,0),COUNT(*),SUM(size) FROM grouped GROUP BY first ORDER BY first DESC LIMIT $limit;
            """;
        groups.Parameters.AddWithValue("$id", id);
        groups.Parameters.AddWithValue("$end", end);
        groups.Parameters.AddWithValue("$limit", limit);
        var start = end;
        long characters = 2;
        long selected = 0;
        using (var reader = await groups.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var groupCharacters = reader.GetInt64(2);
                if (characters + groupCharacters > SessionHistoryPages.MaximumPageCharacters)
                {
                    if (selected == 0) return new([], end, total, end > 0, false,
                        "The next whole message group exceeds the history page character budget. Read the original evidence with an exact source reference.");
                    break;
                }
                start = reader.GetInt32(0);
                characters += groupCharacters;
                selected += reader.GetInt64(1);
                if (selected >= limit) break;
            }
        }
        using var messages = connection.CreateCommand();
        messages.Transaction = transaction;
        messages.CommandText = $"""
            SELECT {DisplayMessageSql},CASE WHEN json_type(j.value,'$.continuation')='object'
                OR json_array_length(j.value,'$.images')>0 THEN 1 ELSE 0 END
            FROM sessions s,json_each(s.payload,'$.messages') j WHERE s.id=$id AND CAST(j.key AS INTEGER)>=$start
                AND CAST(j.key AS INTEGER)<$end ORDER BY CAST(j.key AS INTEGER);
            """;
        messages.Parameters.AddWithValue("$id", id);
        messages.Parameters.AddWithValue("$start", start);
        messages.Parameters.AddWithValue("$end", end);
        List<ChatMessage> result = [];
        var omitted = false;
        using (var reader = await messages.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
            {
                result.Add(JsonSerializer.Deserialize<ChatMessage>(reader.GetString(0), Wire.Json)
                    ?? throw new InvalidDataException("Invalid stored message."));
                omitted |= reader.GetInt32(1) != 0;
            }
        return new(result, start, total, start > 0, OmittedPayloads: omitted);
    }

    public async Task<PendingInputPage> PendingInputsAsync(string id, int limit = 128, CancellationToken cancellationToken = default)
    {
        SessionHistoryPages.ValidateLimit(limit);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var page = await ReadPendingInputsAsync(connection, transaction, id, limit, cancellationToken);
            transaction.Commit();
            return page;
        }
        finally { _gate.Release(); }
    }

    private static async Task<PendingInputPage> ReadPendingInputsAsync(SqliteConnection connection, SqliteTransaction transaction,
        string id, int limit, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            WITH checkpoint AS (
                SELECT json_extract(payload,'$.runCheckpoint.version') AS version,
                    COALESCE(json_extract(payload,'$.runCheckpoint.lastAppliedInputSequence'),0) AS watermark FROM sessions WHERE id=$id
            ), queued AS (
                SELECT sequence,json_extract(payload,'$.runId') AS run_id,json_extract(payload,'$.text') AS text,
                    ROW_NUMBER() OVER(PARTITION BY json_extract(payload,'$.runId'),json_extract(payload,'$.text') ORDER BY sequence) AS ordinal
                FROM events WHERE session_id=$id AND json_extract(payload,'$.type')='input.queued'
            ), applied AS (
                SELECT json_extract(payload,'$.runId') AS run_id,json_extract(payload,'$.text') AS text,COUNT(*) AS count
                FROM events WHERE session_id=$id AND json_extract(payload,'$.type')='input.steered'
                GROUP BY json_extract(payload,'$.runId'),json_extract(payload,'$.text')
            ), pending AS (
                SELECT q.sequence,q.run_id,q.text,c.version FROM queued q CROSS JOIN checkpoint c
                LEFT JOIN applied a ON q.run_id=a.run_id AND q.text=a.text
                WHERE (c.version IS NOT NULL AND q.sequence>c.watermark) OR (c.version IS NULL AND q.ordinal>COALESCE(a.count,0))
            ) SELECT sequence,run_id,text,length(text),COUNT(*) OVER(),version FROM pending ORDER BY sequence LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$limit", limit);
        List<PendingRunInput> result = [];
        var total = 0;
        var legacy = false;
        long characters = 2;
        using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            total = reader.GetInt32(4);
            legacy = reader.IsDBNull(5);
            var length = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
            // JSON escaping can expand a character to six characters; bound the serialized response conservatively.
            if (characters + length * 6 + 256 > SessionHistoryPages.MaximumPageCharacters) break;
            characters += length * 6 + 256;
            result.Add(new(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? "" : reader.GetString(2)));
        }
        if (total > 0 && result.Count == 0)
            return new([], total, true, false, "The next pending instruction exceeds the recovery page budget. Inspect its exact source event before continuing.", legacy);
        return new(result, total, total > result.Count, LegacyRecovery: legacy);
    }

    /// <summary>Call once while the Host owns the data directory, before accepting runs. Never replays tools.</summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE sessions SET payload=json_set(payload,'$.info.status','interrupted','$.info.updatedAt',$now),updated_at=$now WHERE json_extract(payload,'$.info.status') IN ('running','waiting_approval');";
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            var interrupted = await update.ExecuteNonQueryAsync(cancellationToken);
            transaction.Commit();
            return interrupted;
        }
        finally { _gate.Release(); }
    }

    private static AgentSession Deserialize(string json) => JsonSerializer.Deserialize<AgentSession>(json, Wire.Json) ?? throw new InvalidDataException("Invalid stored session; the database was preserved.");
    public void Dispose() => _gate.Dispose();
}
