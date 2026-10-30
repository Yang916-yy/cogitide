**English** | [简体中文](core-memory-design.zh-CN.md)

# Long tasks, project rules and experience reuse design

Historical design/protocol dated **2026-10-30**, targeting ordinary hosted DeepSeek/Claude/OpenAI APIs without training, hidden reasoning or server KV control. At the recorded update, sourced evidence, Pi summaries/checkpoints, hierarchical rules/local skills and project procedure candidates existed; full proposed delta learning and measured paper gains did not. Later implementation reports supersede the then unimplemented items.

First establish whole tool groups, failure fallback, durable raw events and consistent recovery; then test ACE inspired small entry updates with project source/version invalidation. Keep in task work state separate from cross task reusable procedures. Fixed mature references are Pi compaction `e7a0df27c09d11bf4161d3605246cbf57a129cb2` and OpenCode inbox/execution `60072747957926e7aeec7e911fe6e23abe9bf830`, with retained MIT notices and later per file port records. BCL JSON/SHA-256/cancellation/records and existing SQLite are sufficient; no TS runtime/network/database migration, graph/vector service or training framework is needed by default.

Primary mechanisms: ACE incremental playbook/deltas and separated generator/reflector/curator; A-Mem contextual notes/relations; APC completed trajectory templates with current precondition adaptation; GEPA offline reflected candidate search with frozen independent feedback. These references do not establish coding gains or factual truth. MemAgent RL, AgentFlow Flow-GRPO and JitRL logits requirements exceed ordinary API only reproduction. No author research code/prompts were copied by this design.

The proposed TaskState contains schema/state/session/workspace, covered event sequence/digest, verbatim instruction references, goals/constraints/decisions, evidence/hypotheses/questions/next steps and selected procedure versions. SourceRef identifies event/tool/content hash; Evidence includes observation/outcome/scope/artifacts/version/validity; MemoryItem carries source/preconditions/status/supersession; MemoryDelta has expected version/digest and add/amend/supersede/invalidate operations. Hashes prove origin consistency, not truth. Zero exit proves only a command exited successfully. Current/Unknown must reflect captured dependency/environment/test scope rather than pretending file hashes prove everything. Hypotheses stay separate; recalled text never becomes higher priority instruction or automatic execution.

The six stage protocol chooses a complete tool boundary within a conservative character/token estimate; extracts pinned deterministic sources/instructions; proposes bounded schema checked deltas; validates source/version/pairing/state/budgets; commits snapshot/delta/boundary atomically in SQLite only on complete success; then assembles checked state plus raw suffix and revalidates on restart. Stale versions cannot overwrite new steering. Cancelled/bad/overbudget candidates do not advance the boundary. Required user text over budget causes explicit stop. Provider signed/native reasoning is never summarized/reformatted or moved to a different provider.

Rules are an independent mature baseline: fixed precedence/scoped ancestors, versions/hash/read time, root to target reload before affected tools, merged cross path rules and link/size constraints. Skills load catalog before bodies and freeze selected versions; loading never runs scripts or grants permissions. Local discovery precedes online markets. Product runtime skills differ from the developer's own Codex skills. Model experience is not written into AGENTS.md.

APC inspired procedures retain applicability/preconditions/intent/parameters/validation/failure cases/source, progressing from candidate through independent verification rather than one successful trace becoming universal fact. Begin with bounded project/path/tag/keyword retrieval; semantic/A-Mem relations are separate ablations. GEPA optimization stays offline, with development/selection/final tests separated and active versions fixed.

Hypothesis H1 compares incremental states against whole rewritten summaries for constraints, stale evidence and long coding recovery under equal model/tools/window/time/cost. Deterministic fault cases cover repeated compaction, steering races, changed files, forged success/instructions, tool/native state pairing, bad JSON/cancellation/budgets, UI crashes and workspace/model switches. Passing these verifies state machinery rather than intelligence. Proposed arms: A recent whole rounds, B Pi summary, C incremental source/version state, D isolated C without source/invalidation and later E C plus procedures. About twenty development/at least forty family isolated holdouts were proposals, not completed samples or power guarantees. Independent external acceptance, paired intervals, all failures and complete inference/summary usage must be retained. No leaked reference patches, altered tests or post hoc task deletion is permitted; if C does not beat B, keep the mature baseline and the experiment opt in.

This English page summarizes the preserved source. [The full Chinese record](core-memory-design.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](../docs/continuity-validation.md)
- [Original reference 2](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/coding-agent/docs/compaction.md)
- [Original reference 3](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/LICENSE)
- [Original reference 4](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/session/inbox.ts)
- [Original reference 5](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/session/execution.ts)
- [Original reference 6](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/LICENSE)
- [Original reference 7](opensource.md)
- [Original reference 8](https://proceedings.iclr.cc/paper_files/paper/2026/hash/8a94ff6f922d995d7d3f4ebf4143e442-Abstract-Conference.html)
- [Original reference 9](https://proceedings.neurips.cc/paper_files/paper/2025/hash/19909c36f51abc4856b4560aff3d36d6-Abstract-Conference.html)
- [Original reference 10](https://proceedings.neurips.cc/paper_files/paper/2025/hash/9549f7d06700f0966d5f938f1d11022a-Abstract-Conference.html)
- [Original reference 11](https://proceedings.iclr.cc/paper_files/paper/2026/hash/0e9e708b6f48e14fd0ac29e167413f76-Abstract-Conference.html)
- [Original reference 12](https://arxiv.org/abs/2507.19457v2)
- [Original reference 13](https://arxiv.org/html/2510.04618v2)
- [Original reference 14](papers.md)
