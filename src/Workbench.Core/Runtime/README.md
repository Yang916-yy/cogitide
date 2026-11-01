**English** | [简体中文](README.zh-CN.md)

# Shared agent runtime

`AgentEngine.cs` owns the model/tool loop, approval, steering, bounded context assembly, rule reload, durable events, cancellation, terminal outcomes and resource cleanup. All models use it. `Workbench` assemblies/namespaces and data paths remain compatible with the Cogitide product name. The [Host](../../Workbench.Host/README.md) owns workspace exclusion.

Adapters supply inference/native state, without a second execution loop. Tool results are persisted first; UI loss does not replay old actions. Run completion waits for owned background resource cleanup, preserving failed/unknown status if cleanup cannot be confirmed.

Research integration reuses [ACE/APC inspired sourced procedures](../Memory/README.md), optional [ACON guideline compaction](../Context/README.md), run bound [RepoMem/DCI history search](../../Workbench.Tools/RepositoryMemory/README.md) and hash checked [read only prefetch](../Research/Speculation/README.md). [GEPA/SkillOpt](../Research/SkillOptimization/README.md) is explicit offline work and never starts or replaces skills from normal chat. References cannot override user instructions or widen permissions.

Engineering additions are durable source/version checks, steering boundaries, no unknown action replay and owned resource lifetimes. These are not full paper reproductions or proved novelty/task gains. Focused Pi/OpenCode cut points and skill discovery are documented in [Compaction provenance](../Licenses/Compaction/PROVENANCE.md) and [PROJECT-REUSE](../PROJECT-REUSE.md), at commits `e7a0df27c09d11bf4161d3605246cbf57a129cb2` / `60072747957926e7aeec7e911fe6e23abe9bf830`; whole upstream runtimes/event buses/package managers were not copied.

Concurrency uses `SemaphoreSlim`, `ConcurrentDictionary` and `CancellationToken`; streams use `IAsyncEnumerable`; tools use `FrozenDictionary`; C# closed hierarchies express outcomes; the existing SQLite store persists state.

Optional `RequireOsIsolation` replaces the ordinary workspace-tool catalog with a broker-owned worker catalog. Approvals and rule checks run in this kernel before execution; project-rule/skill/experience source bytes are read through the exact chat/run worker context. Only the kernel's own evidence/skill/experience instances retain trusted internal-store access. Unsupported isolation and incomplete cleanup fail explicitly; account/model credentials stay outside the worker. [Native policy and backend limitations](../../Workbench.Tools/Isolation/README.md) describe the actual OS boundary. This is an engineering security mechanism, not a paper-derived performance claim; deterministic broker fixtures and real native-denial acceptance provide different evidence.

Normal runs default to 256 inference iterations and six hours, with explicit bounded configuration up to 10,000 iterations and seven days. These are Cogitide engineering defaults, not upstream or provider limits. Iteration, time and optional provider-reported token limits pause the task with `run.paused`; cancellation remains a distinct outcome. A persisted `RunCheckpoint` records the original active instruction boundary, options, usage, parent run and stop reason. `ContinueAsync` starts only on an explicit request with a fresh budget, reloads current project rules and retains original instructions and complete historical tool groups. Stored tool calls are never an execution queue. Interrupted calls receive explicit unknown results requiring inspection.

`RunUsageMeter` wraps the same `IAgentModel` for ordinary inference and compaction, counting cumulative per-call provider input/output reports once, including failed or cancelled streams. Observed totals are lower bounds when usage is absent or a stream does not finish; `CallsWithUnknownUsage` and `IncompleteCalls` preserve those uncertainties. An explicit reported-token cap pauses when reported usage reaches the cap or a completed call omits usage. Provider reporting can arrive after work is charged: this is neither an exact tokenizer nor a hard monetary limit, and no unknown dollar amount is invented. Partial assistant/native state and unfinished summaries are never committed on a limit stop.

Accepted steering is an append-only `input.queued` source event. Applying it saves user messages and the last applied event sequence in the same session snapshot before publishing `input.steered`. Recovery reads bounded pending instructions and resumes at the model boundary, including when publication was lost after the snapshot commit. Oversized or incomplete recovery refuses a continuation instead of dropping accepted instructions. Checkpointless legacy pending inputs are ambiguous because the user message may already have been saved before notification loss; runs refuse automatic rehydration and require review followed by a new chat with the confirmed task. Original history and source events remain unchanged. These are durable application recovery guarantees, not OS process checkpointing: background processes are still cleaned up and never automatically restarted.

**Do not reinvent infrastructure.** Every entry point reuses AgentEngine and existing model/network/JSON/storage/process adapters. New infrastructure needs concrete constraints, alternatives and tests without inventing approval gates. Research changes remain switchable and need independent comparisons against the same baseline.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](AgentEngine.cs)
- [Original reference 2](../Research/Speculation/ReadOnlyPrefetch.cs)
