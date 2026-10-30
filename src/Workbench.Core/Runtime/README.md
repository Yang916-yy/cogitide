**English** | [简体中文](README.zh-CN.md)

# Shared agent runtime

`AgentEngine.cs` owns the model/tool loop, approval, steering, bounded context assembly, rule reload, durable events, cancellation, terminal outcomes and resource cleanup. All models use it. `Workbench` assemblies/namespaces and data paths remain compatible with the Cogitide product name. The [Host](../../Workbench.Host/README.md) owns workspace exclusion.

Adapters supply inference/native state, without a second execution loop. Tool results are persisted first; UI loss does not replay old actions. Run completion waits for owned background resource cleanup, preserving failed/unknown status if cleanup cannot be confirmed.

Research integration reuses [ACE/APC inspired sourced procedures](../Memory/README.md), optional [ACON guideline compaction](../Context/README.md), run bound [RepoMem/DCI history search](../../Workbench.Tools/RepositoryMemory/README.md) and hash checked [read only prefetch](../Research/Speculation/README.md). [GEPA/SkillOpt](../Research/SkillOptimization/README.md) is explicit offline work and never starts or replaces skills from normal chat. References cannot override user instructions or widen permissions.

Engineering additions are durable source/version checks, steering boundaries, no unknown action replay and owned resource lifetimes. These are not full paper reproductions or proved novelty/task gains. Focused Pi/OpenCode cut points and skill discovery are documented in [Compaction provenance](../Licenses/Compaction/PROVENANCE.md) and [PROJECT-REUSE](../PROJECT-REUSE.md), at commits `e7a0df27c09d11bf4161d3605246cbf57a129cb2` / `60072747957926e7aeec7e911fe6e23abe9bf830`; whole upstream runtimes/event buses/package managers were not copied.

Concurrency uses `SemaphoreSlim`, `ConcurrentDictionary` and `CancellationToken`; streams use `IAsyncEnumerable`; tools use `FrozenDictionary`; C# closed hierarchies express outcomes; the existing SQLite store persists state.

**Do not reinvent infrastructure.** Every entry point reuses AgentEngine and existing model/network/JSON/storage/process adapters. New infrastructure needs concrete constraints, alternatives and tests without inventing approval gates. Research changes remain switchable and need independent comparisons against the same baseline.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](AgentEngine.cs)
- [Original reference 2](../Research/Speculation/ReadOnlyPrefetch.cs)
