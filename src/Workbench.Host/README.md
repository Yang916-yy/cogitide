**English** | [简体中文](README.zh-CN.md)

# Application Host and explicit research entry points

`Program.cs` owns stdio requests/events, instance locks, workspace/session run ownership, account/model assembly, the shared AgentEngine, approvals and background command workers. [ResearchCommandLine](Research/ResearchCommandLine.cs) validates explicitly started skill/compression optimization and adapts frozen repository tasks to actual model usage accounting.

Host/client separation references mature Codex/OpenCode architecture; the versioned wire schema and assembly code are project maintained, not a wholesale app server port. [Tools provenance](../Workbench.Tools/DEPENDENCIES.md) records fixed behavior sources; [Models](../Workbench.Core/Models/README.md) and [Auth](../Workbench.Auth/Accounts/README.md) use official SDKs. All models enter one [AgentEngine](../Workbench.Core/Runtime/README.md); login supplies authentication, not delegated Codex execution.

BCL networking, Process, JSON, cancellation and synchronization plus the existing SQLite store supply infrastructure. Host owns locks and run resources; disconnect/stop never authorizes replay. Research CLI is explicit batch work, with algorithms in [SkillOptimization](../Workbench.Core/Research/SkillOptimization/README.md) and [Context](../Workbench.Core/Context/README.md). Host only adds real tasks, frozen tests and budgets. Exported results remain candidates without installation or increased permissions. Report tasks/model/baseline/budget separately from protocol fixture or paper reproduction claims.

**Do not reinvent infrastructure.** Reuse Core/Tools/Auth/Protocol instead of a CLI model loop, HTTP/SSE, database, command runner or approval service. A new adapter requires a concrete interface gap, mature choice and lifecycle tests rather than upstream directory imitation.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](Program.cs)
