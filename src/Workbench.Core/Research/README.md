**English** | [简体中文](README.zh-CN.md)

# Explicit or switchable research mechanisms

[SkillOptimization](SkillOptimization/README.md) provides GEPA/SkillOpt inspired offline text search, freezing selection before final holdout evaluation and never installing a candidate. [Speculation](Speculation/README.md) overlaps a predicted file read with inference, consuming it only after exact request and current hash checks. [Context](../Context/README.md) owns ACON guideline compaction and offline comparison.

These independent C# mechanisms reuse `IAgentModel`, `IAgentTool`, AgentEngine, BCL Tasks/CancellationToken/SHA-256/System.Text.Json and existing SQLite. Child READMEs cite primary papers, mechanism scope, project additions and test boundaries. No author research source was imported into these independent implementations, and a fixture pass is not a paper benefit reproduction.

**Do not reinvent infrastructure.** Policies/results belong here; models, tools, execution, process ownership and storage stay in the existing kernel/mature .NET components. Research is explicit or disabled by default and never expands budget or activates a candidate by itself.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.
