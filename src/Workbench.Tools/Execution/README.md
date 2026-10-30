**English** | [简体中文](README.zh-CN.md)

# Background command sessions

`CommandSessions.cs` implements `start_command`, `read_command` and `stop_command` with ownership, log cursors, state validation and recovery. A hidden Host subprocess owns each command; parent stdin EOF cancels the shell and its process tree when the Host disappears.

Session IDs, yielding, polling, running/terminal states and bounded output are behavior references to Codex unified exec, fixed commit `788b9d7bf6e37f735647b7f84b556c2a807df4d8`. [Commands provenance](../Licenses/Commands/PROVENANCE.md) retains source mappings, Apache 2.0 LICENSE/NOTICE and deliberate exclusions. This is not a Rust runtime/sandbox port and does not invoke the Codex executable.

Execution reuses .NET `Process`, `FileStream`, `CancellationToken`, `SemaphoreSlim`, `ConcurrentDictionary`, System.Text.Json source generation and [WorkspaceService](../Workspace/README.md). Parent pipe ownership, log restoration and run ownership are project engineering additions rather than a learning algorithm.

Commands belong to the current run. Cancellation, run completion and Host loss stop owned active commands. A later run in the same chat can read retained logs; restart neither attaches to an old PID nor replays a command. Logs have explicit budgets and UTF-8 byte cursors. A running ID is not proof of completion. BCL process tree termination is not an OS sandbox or a guarantee about deliberately detached descendants. User interactive PTY sessions belong to [Desktop](../../Workbench.Desktop/README.md).

On Windows, status replacement uses BCL `File.Replace`; initial creation uses `File.Move`. Deterministic shared handle evidence and [runtime #114230](https://github.com/dotnet/runtime/issues/114230) informed the replacement. Reads retry only transient missing path/sharing errors, with cancellable total backoff of at most 310 ms. Permanent errors and corrupt state remain errors; commands are never retried. Diagnostics retain bounded stage/type/HResult without command text, paths or credentials. This does not guarantee that the path is openable at every instant of kernel replacement.

**Do not reinvent infrastructure.** Reuse this lifecycle and mature BCL/PTY components. Extensions must document source versions and cancellation, parent loss, Unicode and output budget evidence; background execution never bypasses ownership or terminal state checks.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](CommandSessions.cs)

## Pinned-runtime macOS process cleanup

The pinned .NET 11 RC macOS runtime has a documented `Process.Kill(entireProcessTree: true)` SIGSTOP/child-reaper regression: [runtime issue 131944](https://github.com/dotnet/runtime/issues/131944), [upstream backport 135409](https://github.com/dotnet/runtime/pull/135409), commit `c8b56e6c12e3010e74e1724487a65cbb1c75d0d1`. `ProcessStartInfo.CreateNewProcessGroup` is Windows-only in this SDK and cannot solve it on Unix.

Until the fixed runtime is deployed, the shared macOS stop helper uses the existing system `/bin/ps` for a bounded PID/parent snapshot and BCL single-process `Kill()` in descendants-first order, without SIGSTOP or process-group signals. Start-time checks reject recycled PID identities. Other platforms keep the existing BCL tree operation. Commands and macOS MCP clients share this helper; no second process supervisor, native interop or MCP protocol parser was added.

This is a targeted workaround for a measured runtime gap. The snapshot cannot contain new forks or deliberately detached descendants and is not an OS sandbox. Inventory/cleanup failures are reported as unverified cleanup, never successful command execution. Tests exercise owned parent/child cancellation, failing stdout/stderr retention and a subsequent fresh process to check the runtime reaper remains usable.
