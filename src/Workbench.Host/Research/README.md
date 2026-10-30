**English** | [简体中文](README.zh-CN.md)

# Research CLI: explicit offline evaluation and exports

This peripheral adapter reuses AgentEngine, official model SDKs, WorkspaceService argv execution, Git and SQLite. Algorithms remain in [SkillOptimization](../../Workbench.Core/Research/SkillOptimization/README.md) and [Context](../../Workbench.Core/Context/README.md). No parallel agent/network/scheduler is introduced.

```text
Workbench.Host --optimize-skill SPEC.json --output NEW_DIRECTORY --validate-only
Workbench.Host --optimize-skill SPEC.json --output NEW_DIRECTORY --execute
Workbench.Host --optimize-compression SPEC.json --output NEW_DIRECTORY --execute
```

`--validate-only` validates without keys, model calls, command execution or output creation. `--execute` can incur cost; keys are read only from `provider.apiKeyEnvironmentVariable`, never embedded in specs. All inference, summaries and proposals count. In flight requests cannot be revoked by an observed budget; missing usage stays unknown.

`ResearchOptimizationSpec` / `ResearchTaskPayload` define the schema. [verify-research.py](../../../scripts/verify-research.py) generates local SSE fixtures without paid services. Specs contain provider, initialSkill, strategy/budgets, split tasks/families; payloads contain an absolute local repository, full baseCommit, prompt, validationCommands, protectedPaths and optional allowedCommands. Compression adds development only failures/budget.

Each evaluation creates a new Git repository and shallow fetches only the frozen commit, without future refs or changes to the input repository. The shared kernel runs against frozen external validation. Protected direct write/patch is rejected; hashes are checked before approval and validation, and changed acceptance skips execution/fails. Custom key variables are removed from tool child environments. Selection freezes before holdout, whose feedback cannot select candidates. Exports never overwrite active skills.

Artifacts retain candidates/reports/events/validation output/usage/terminal state. Ctrl+C uses normal cancellation; unknown or overbudget work is not free success. Local scripts/repositories/allowed commands must be trusted: workspace separation and application permissions are **not an OS sandbox** against malicious scripts that edit/restore files, read Host files or fake tests.

Project additions are frozen revisions, the real shared kernel, protected acceptance, end to end usage and nonactivation; they are neither a novel optimization algorithm nor paper benefit reproduction. **Do not reinvent infrastructure:** reuse existing execution/models/JSON/hash/storage; custom code needs a specific gap and cannot bypass approval, budgets or split isolation.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.
