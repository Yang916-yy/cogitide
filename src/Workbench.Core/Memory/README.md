**English** | [简体中文](README.zh-CN.md)

# Evidence and project experience

`EvidenceMemory` projects observed events into bounded evidence, artifacts, sources and conservative stale states, including deleted/moved files and running background commands. `SessionEvidenceTool` exposes session bound `inspect_evidence`. `ProjectExperienceTools` implements `remember_experience` / `recall_project_memory`: project scoped deduplication, explicit replacement, prerequisites and version checks. `EvidenceFileHash` shares one bounded streaming hash across modules.

Mechanism references are [ACE v2](https://arxiv.org/html/2510.04618v2)'s incremental playbook entries and [APC](https://proceedings.neurips.cc/paper_files/paper/2025/hash/9549f7d06700f0966d5f938f1d11022a-Abstract-Conference.html)'s conditional procedure reuse. Only inspectable procedure candidates are implemented: no learned retriever, full ACE Generator/Reflector/Curator or complete APC system. Engineering additions are real tool source events, version/deletion provenance, replay checks and current hashes of reported files. Reappearance invalidates deletion evidence. Model suggestions remain `Candidate`; unchanged reported files do not prove dependencies, environment or tests remain valid.

Project procedures, [Git history localization](../../Workbench.Tools/RepositoryMemory/README.md) and [offline skill optimization](../Research/SkillOptimization/README.md) are separate mechanisms; [PROJECT-REUSE](../PROJECT-REUSE.md) records provenance. Combining them is not proved novelty or measured benefit. Recalled commands are never automatically executed.

The implementation reuses the [SQLite event store](../Persistence/README.md), BCL SHA-256, async cancellation, generated JSON metadata, immutable records and bounded keyword matching. No vector/graph service or second storage layer is added. Save validates that a complete single recall package fits its retrieval budget.

**Do not reinvent infrastructure.** Reuse event storage and hashing. A new semantic/index layer needs a measured retrieval gap, mature alternatives and migration/comparison evidence; it cannot discard failures, grant permissions or replace frozen validation with the producing model's own judgment.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](EvidenceMemory.cs)
- [Original reference 2](SessionEvidenceTool.cs)
- [Original reference 3](ProjectExperienceTools.cs)
- [Original reference 4](EvidenceFileHash.cs)
