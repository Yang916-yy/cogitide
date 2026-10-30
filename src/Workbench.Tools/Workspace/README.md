**English** | [简体中文](README.zh-CN.md)

# Workspace primitives

`WorkspaceService.cs` owns path boundaries, whole file read/write and unique replacement, Git diff, shell/argv execution and shared process/budget helpers. Registration stays in [WorkspaceTools.cs](../WorkspaceTools.cs); callers reuse this service instead of assembling another file access policy.

The implementation reuses .NET `Path`, `FileStream`, strict UTF-8, SHA-256, `Process`, asynchronous redirected streams and cancellation. **Git CLI** owns status/diff/ignore behavior; no Git object, diff or ignore parser is reimplemented. [Tools dependencies](../DEPENDENCIES.md) records versions and provenance. This file is application integration of BCL components, not a source port of another project.

Product policy adds checked workspace paths and link rejection, current file hashes, unique edits, output budgets and owned process cancellation. These checks are not an OS sandbox: shell commands and their children run with the user's privileges and can access resources outside the workspace. More specialized code belongs in [Editing](../Editing/README.md), [Navigation](../Navigation/README.md) and [Execution](../Execution/README.md).

**Do not reinvent infrastructure.** Reuse this path/hash/process boundary and existing .NET/Git behavior. A replacement needs an actual limitation, compared alternatives and concurrency/cancellation/Unicode evidence.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](WorkspaceService.cs)

## Pinned-runtime macOS process cleanup

The pinned .NET 11 RC macOS runtime has a documented `Process.Kill(entireProcessTree: true)` SIGSTOP/child-reaper regression: [runtime issue 131944](https://github.com/dotnet/runtime/issues/131944), [upstream backport 135409](https://github.com/dotnet/runtime/pull/135409), commit `c8b56e6c12e3010e74e1724487a65cbb1c75d0d1`. `ProcessStartInfo.CreateNewProcessGroup` is Windows-only in this SDK and cannot solve it on Unix.

Until the fixed runtime is deployed, the shared macOS stop helper uses the existing system `/bin/ps` for a bounded PID/parent snapshot and BCL single-process `Kill()` in descendants-first order, without SIGSTOP or process-group signals. Start-time checks reject recycled PID identities. Other platforms keep the existing BCL tree operation. Commands and macOS MCP clients share this helper; no second process supervisor, native interop or MCP protocol parser was added.

This is a targeted workaround for a measured runtime gap. The snapshot cannot contain new forks or deliberately detached descendants and is not an OS sandbox. Inventory/cleanup failures are reported as unverified cleanup, never successful command execution. Tests exercise owned parent/child cancellation, failing stdout/stderr retention and a subsequent fresh process to check the runtime reaper remains usable.
