**English** | [简体中文](README.zh-CN.md)

# Session and event persistence

`SqliteSessionStore` implements `ISessionStore`: sessions/messages/checkpoints, ordered per session event append, schema versions and recovery state. Unknown newer schemas are rejected rather than overwritten by a silent downgrade.

The adapter reuses **Microsoft.Data.Sqlite 10.0.14**, SQLite transactions/WAL, parameterized commands, System.Text.Json, BCL paths/async cancellation and `SemaphoreSlim`. Versions are pinned in [the project](../Workbench.Core.csproj) and [lock file](../packages.lock.json). It is application use of a mature database, not a new storage service or a source port.

Snapshots and raw events have separate roles. A summary is a replaceable view; original messages survive. Restart must never replay a command merely because it occurs in stored text. A successful save is not task success. User code, attachments, tool output and provider state are private runtime data and must not enter Git.

This layer supplies traceable recovery to [Runtime](../Runtime/README.md) and [Memory](../Memory/README.md), without claiming a new learning algorithm. Some queries still load the selected event collection; end to end constant memory and completely paginated retrieval are not claimed.

**Do not reinvent infrastructure.** Reuse this store and SQLite transactions/indexes/migrations. A replacement needs measured bottlenecks, compatible migration, recovery tests and comparison with mature alternatives.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](SqliteSessionStore.cs)
