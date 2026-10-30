**English** | [简体中文](README.zh-CN.md)

# Core: one shared agent kernel

Every model enters [Runtime](Runtime/README.md). [Models](Models/README.md) adapts official SDKs; [Persistence](Persistence/README.md) uses SQLite. [Projects](Projects/README.md) owns rules/skills, [Memory](Memory/README.md) owns source evidence and candidate procedures, and [Context](Context/README.md) owns compaction/guidelines. Optional search and prefetch experiments live in [Research](Research/README.md). Interfaces are shared through Protocol rather than a parallel agent.

Fixed upstream sources and original licenses live in `Licenses/`; focused Pi/OpenCode behavior uses commits `e7a0df27c09d11bf4161d3605246cbf57a129cb2` and `60072747957926e7aeec7e911fe6e23abe9bf830`. Each kernel module distinguishes primary paper mechanisms, engineering additions, exclusions and functional tests. Citations do not establish performance gains. Research paths preserve the same tool, approval and storage boundaries as the baseline.

**Do not reinvent infrastructure.** Reuse official SDK HTTP/SSE, System.Text.Json, Microsoft.Data.Sqlite and BCL cryptography/cancellation/concurrency. A custom component requires a missing capability, alternatives and validation. File statistics are generated in the [module map](../../docs/module-map.md).

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.
