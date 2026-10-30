# Cross-domain mechanisms for agent and tool optimization

Primary-source review: **2026-10-30**. This is a design and experiment record: the proposed agent interfaces were not implemented in this research pass, while a separately conducted, rejected metadata-cache experiment is identified below. This research subtask ran no SDK commands, paid evaluations, model training, or desktop sessions. Historical measured .NET tool costs belong to [the local performance baseline](../docs/performance-baseline.json); later before/after reports must identify their own exact source and workload.

The most useful transfers are selective work, explicit dependency/version tracking, and observations that can be independently checked. They can be implemented through ordinary model APIs and the existing C# kernel. None requires replacing the HTTP stack, parser, scheduler, storage engine, or approval loop. Engineering integration of these ideas is not a claim of academic novelty.

## Existing capability is the comparison baseline

Before adding a tool, account for what Cogitide already does:

- [Repository inventory](../src/Workbench.Tools/Navigation/RepositoryInventory.cs) narrows a directory before inventory budgets and delegates ignore semantics to Git.
- [Search and reads](../src/Workbench.Tools/Navigation/NavigationTools.cs) already support literal/regex/glob filters, matching-line pagination, up to ten surrounding context lines, bounded previews, full-file hashes, and stale-page rejection.
- [AgentEngine](../src/Workbench.Core/Runtime/AgentEngine.cs) accepts multiple tool calls in one assistant response. A batch wrapper does not automatically eliminate additional model round trips compared with that existing interface.
- [SessionEvidenceTool](../src/Workbench.Core/Memory/SessionEvidenceTool.cs) already recalls original persisted observations by event sequence. [EvidenceMemory](../src/Workbench.Core/Memory/EvidenceMemory.cs) tracks provenance and staleness. A new evidence service must not duplicate their store.
- [Semantic navigation](../src/Workbench.Tools/Navigation/SemanticNavigation.cs) provides bounded, source-only Roslyn binding; it rereads sources rather than trusting an unverified private-source cache. [ReadOnlyPrefetch](../src/Workbench.Core/Research/Speculation/ReadOnlyPrefetch.cs) already offers an explicit, revocable single-read experiment.

These facts rule out calling an ordinary `read_files` wrapper, context snippets, native multi-call support, or a metadata cache a new research mechanism.

## 1. Database query planning and projection

**Evidence.** Leis et al., *How Good Are Query Optimizers, Really?*, PVLDB 9(3), 2015, separates cardinality estimation, cost modeling, and plan enumeration using a realistic multi-join workload. It demonstrates that inaccurate estimates can dominate plan quality; it does not evaluate agents or repository search. This is controlled systems evidence within its database workload, not a transferable agent speedup. [Publisher paper](https://www.vldb.org/pvldb/vol9/p204-leis.pdf).

**Verified implementation reference.** DuckDB's official filter-pushdown implementation is pinned to **v1.4.3 / `d1dc88f950d456d72493df452dabdcd13aa413dd`**, [source](https://github.com/duckdb/duckdb/blob/d1dc88f950d456d72493df452dabdcd13aa413dd/src/optimizer/filter_pushdown.cpp), [MIT license](https://github.com/duckdb/duckdb/blob/d1dc88f950d456d72493df452dabdcd13aa413dd/LICENSE). It is a mature implementation reference, not the JOB paper's artifact. The frequently linked [JOB distribution](https://github.com/gregrahn/join-order-benchmark/blob/a39603662e023e449cb2121997a5034df9e02ebf/README.md) explicitly says it is not maintained by the original authors; it must not be described as author code.

**Cogitide transfer — inference/design.** Treat `inventory → path/glob filter → text match → context projection → serialization` as operators with explicit input/output budgets. Reuse the current Git inventory, Microsoft globbing, BCL regex and bounded JSON writer. Record actual files/bytes/matches emitted by each stage before introducing a learned cost model. A useful extension is a source-bound projection of an existing observation, rather than a second full read or a new index.

**Cost and failure case.** Planning can cost more than a direct scan in a small repository. A narrowly estimated scope can miss a relevant sibling project; heuristic selectivity must never silently change exhaustive-search claims. Search context may already answer the question, so automatically attaching full read pages can increase I/O and model context. A C# semantic reference is not interchangeable with a textual match.

**Controlled experiment.** Compare current search with context, current search followed by the existing same-response multi-read calls, and an optional fused search/read projection. Match output information and total byte/character caps. Include short exact matches, long lines whose useful definition is outside the preview, broad matches, and a misleading narrow prefix. Measure bytes scanned, allocations, returned characters, dependency rounds and exact source-span recall. Report any speedup only at equal coverage and evidence fidelity.

**Decision:** instrument and measure now; defer a general query optimizer and persistent source index.

## 2. Demand-driven incremental computation

**Evidence.** Hammer et al., *Adapton: Composable, Demand-Driven Incremental Computation*, PLDI 2014, formalizes a demanded computation graph and evaluates an OCaml implementation. Its scope is incremental programs with explicit dependencies. This gives a useful model for invalidation, but neither its calculus nor its benchmark results establishes that arbitrary external agent tools are pure or safely cacheable. [Author paper](https://matthewhammer.org/adapton/adapton-pldi2014.pdf), [artifact guide](https://www.cs.umd.edu/projects/PL/adapton/).

**Verified implementation reference.** The author project's surviving Nominal Adapton implementation is **`plum-umd/adapton.ocaml@a8e642ac1cc113b33e1837da960940c2dfcfa772`**, [engine](https://github.com/plum-umd/adapton.ocaml/blob/a8e642ac1cc113b33e1837da960940c2dfcfa772/src/core/engine.ml). This is the later named implementation, not a claim that the original PLDI 2014 artifact was retrieved. The [project's own history](https://adapton.org/#implementations) distinguishes them and records the loss of the old Mercurial prototypes. A clear reusable license was not established for that pinned OCaml tree in this review; no code is imported.

**Cogitide transfer — inference/design.** Separate immutable dependencies from mutable observations. Roslyn runtime metadata may be shared when its identity and lifetime are explicit; workspace source still needs the current request's reads and hashes. For agent evidence, use a dependency tuple such as `(session, source event, workspace path, original hash, query options)` and recheck only the evidence that the current task demands. Reuse `EvidenceMemory`, `EvidenceFileHash`, `SessionEvidenceTool`, and Roslyn's immutable objects. Do not build a general demanded-computation runtime.

**Cost and failure case.** A file watcher, mtime, file size or assembly MVID alone is not a content proof. Hash rechecks still read the bytes and may be slower than rereading a tiny file. Graph/bookkeeping memory can dominate low-reuse sessions. Cancellation must not publish a partially initialized shared object; disposal must not invalidate metadata still used by another compilation. Cross-account or cross-workspace source reuse is outside this proposal.

**Controlled experiment.** Use 0%, 1%, 10% and 100% changed evidence between requests, including same-length/same-timestamp edits, deletion, rename, ignored-path changes and cancellation. Compare full reread against explicit hash recheck plus selective reread. Measure current-state error rate as well as I/O, returned context and retained memory. An unchanged hash can refresh a content observation; it cannot certify compilation or an earlier command's success.

**Measured counterexample and decision.** The integration owner subsequently tested a strong-identity public-metadata cache on a matched local profile. Managed allocation fell from approximately **7.36 MB to 2.65 MB**, but elapsed time regressed from approximately **154 ms** to **187 ms** with synchronous identity reads and **272 ms** with asynchronous identity reads. These are decimal MB and local profile measurements, not the older historical baseline or a production latency guarantee. The cache was **rejected and withdrawn**; no weaker mtime-only cache replaced it. [Current tuning report and experiment scope](../docs/performance-tuning.md). This is negative evidence for that implementation/workload, not a proof that every immutable metadata cache is slower. Differential evidence recheck remains a design candidate; private-source caching and general dependency graphs remain deferred.

## 3. Operating-system scheduling and value of computation

**Evidence.** Mao et al., *Learning Scheduling Algorithms for Data Processing Clusters*, SIGCOMM 2019, learns workload-specific policies over job dependency graphs and evaluates both simulation and a Spark integration. Its training and cluster assumptions differ substantially from desktop API calls. [Author project and paper](https://web.mit.edu/decima/). Hay et al., *Selecting Computations: Theory and Applications*, UAI 2012, provides metalevel selection results and heuristics under specified statistical models; it does not make arbitrary confidence scores calibrated. [Original author paper](https://people.eecs.berkeley.edu/~russell/papers/uai12-meta.pdf).

**Verified implementation reference.** The author-linked Decima simulator is **`hongzimao/decima-sim@c010dd74ff4b7566bd0ac989c90a32cfbc630d84`**, [README](https://github.com/hongzimao/decima-sim/blob/c010dd74ff4b7566bd0ac989c90a32cfbc630d84/README.md), [heuristic baseline](https://github.com/hongzimao/decima-sim/blob/c010dd74ff4b7566bd0ac989c90a32cfbc630d84/heuristic_agent.py). It is the simulator, not the complete Spark artifact. No explicit reusable license or author code for the UAI paper was established here; neither implementation is copied.

**Cogitide transfer — inference/design.** Distinguish tasks that are ready from tasks whose inputs have not been observed. A search-dependent read cannot begin merely because another read can. Within the existing run's fixed resource cap, a deterministic priority can prefer a cheap observation likely to resolve the next decision. A candidate heuristic is estimated decision benefit minus measured local cost and added context cost. This is an experimental score, not a derived optimal policy. Reuse BCL tasks/cancellation and the existing command manager; approvals, writes, steering and cleanup remain serialization boundaries.

**Cost and failure case.** More parallelism can increase contention and cancellation waste; minimum mean completion time can starve an important long task. A value estimator may spend more than the operation it selects, or miss a rare decisive file. A faster model API round trip can erase the apparent benefit of speculative local work. No policy may raise a money budget or execute a previously declined action.

**Controlled experiment.** Replay fixed dependency graphs with fast/slow reads, shared-device contention, a late decisive result, cancellation, and steering. Compare existing order, a fixed bounded ready-only policy and the proposed heuristic at identical concurrency and total-work caps. Measure critical-path time, total work, waste, starvation and invariant violations. Keep model/network latency either observed or explicitly synthetic.

**Decision:** measured deterministic ordering is suitable for a later opt-in experiment; defer RL scheduling and general speculative branching.

## 4. Transactional histories and checkable witnesses

**Evidence.** Kingsbury and Alvaro, *Elle: Inferring Isolation Anomalies from Experimental Observations*, PVLDB 14(3), pages 268–280, uses observable transaction histories to infer dependency anomalies under datatype assumptions. The publisher citation gives 2021; an earlier preprint appeared in 2020. Its soundness claims are scoped, and do not prove a filesystem/agent workflow serializable. [Publisher paper](https://www.vldb.org/pvldb/vol14/p268-alvaro.pdf).

**Verified implementation reference.** Author implementation **`jepsen-io/elle@4e359e597c52938b0afebb8bc42273914d68a424`**, [README and checker scope](https://github.com/jepsen-io/elle/blob/4e359e597c52938b0afebb8bc42273914d68a424/README.markdown), [EPL-2.0 license](https://github.com/jepsen-io/elle/blob/4e359e597c52938b0afebb8bc42273914d68a424/LICENSE). Its own README asks users to check anomaly witnesses and describes an incomplete formal proof artifact. Reusing the testing principle does not require importing its Clojure checker or changing SQLite.

**Cogitide transfer — inference/design.** Specify a small history contract for a tool operation: proposal, applicable rules, approval, start, observed result, persisted result and resource cleanup. An acceptance report should point to exact event sequences that witness success or a violated invariant. Reuse the existing SQLite event store, current hashes and command records. For a mutating call interrupted between effect and observation, preserve `unknown` rather than infer commit or replay it. A multi-file patch remains checked engineering with recovery limits, not an ACID transaction across arbitrary external writers.

**Cost and failure case.** Durable events and full hashes cost writes/I/O. A recorded tool result is not proof of current state. A compensating action can itself fail, so automatic rollback/retry of external tools is unsafe. Absence of an anomaly in finite fixtures is not a proof of correctness.

**Controlled experiment.** Inject faults before launch, after the side effect but before result persistence, while approval is pending, during output publication, and during cleanup. Compare ordinary success-only assertions with event-and-state witness checks. Count duplicate effects, false completed states, unknown outcomes and cleanup leaks. Preserve complete failing histories for diagnosis instead of optimizing the checker against selected passing traces.

**Decision:** add focused witness assertions to existing fixtures now; defer a general transactional checker or saga/rollback engine.

## 5. Compiler-style tool plans

**Evidence.** Kim et al., *An LLM Compiler for Parallel Function Calling*, ICML 2024, separates a function-call planner, task fetching and execution. It evaluates API-friendly parallel orchestration against ReAct on selected tasks. Its reported gains do not establish coding-agent improvement, and do not justify treating an already supported native multi-call response as a sequential baseline. [Official proceedings](https://proceedings.mlr.press/v235/kim24y.html).

**Verified implementation reference.** Author code **`SqueezeAILab/LLMCompiler@a00c9d35507507da70e8c637eee64efc8c1857ae`**, [executor](https://github.com/SqueezeAILab/LLMCompiler/blob/a00c9d35507507da70e8c637eee64efc8c1857ae/src/executors/agent_executor.py), [MIT license](https://github.com/SqueezeAILab/LLMCompiler/blob/a00c9d35507507da70e8c637eee64efc8c1857ae/LICENSE). This review borrows the dependency distinction only; it does not port the Python/LangChain runtime or prompts.

**Cogitide transfer — inference/design.** Preserve explicit data dependencies while reducing repeated projection/serialization work inside an already authorized read operation. A bounded search-and-context operator can reuse bytes already scanned, while retaining source hashes and page positions. A separately supplied list of independent reads can already fit one assistant response; introducing a second generic plan language needs an actual missing capability. Keep dynamic MCP tools, commands and writes outside any automatic reorder/fusion unless their side effects and approvals have a stronger contract.

**Cost and failure case.** A planner call can erase the saved latency. Incorrect independence can run a read before a generated file exists, use stale rules, or execute an action superseded by steering. Aggregating tool outputs can exceed a context cap even if each individual output fits. Fusing reads that existing snippets already answer can make performance worse.

**Controlled experiment.** Include current native multi-call and context-enabled search as strong baselines. Compare equal information under equal global budgets; count actual provider turns, not a manufactured per-file sequential baseline. Test dependent search/read, independent reads, short snippets that suffice, and long context that does not. Report provider latency separately from local tool latency and synthetic delay.

**Decision:** bounded operator fusion is a candidate only after profiling; defer a new planner language or executor.

## 6. Tool interfaces, policy checks and repeated reliability

**Evidence.** Yao et al., *τ-bench: A Benchmark for Tool-Agent-User Interaction in Real-World Domains*, 2024, evaluates policy-constrained tool interactions with a simulated user and final-state checking. Its repeated-trial reliability measure differs from one successful demonstration. The inspected author paper is a preprint; this review assigns no conference presentation label. It is a customer-service benchmark, not repository repair evidence. [Author paper](https://arxiv.org/abs/2406.12045), [author overview](https://sierra.ai/resources/research/tau-bench).

**Verified implementation reference.** Author code **`sierra-research/tau-bench@59a200c6d575d595120f1cb70fea53cef0632f6b`**, [environment and final-state reward](https://github.com/sierra-research/tau-bench/blob/59a200c6d575d595120f1cb70fea53cef0632f6b/tau_bench/envs/base.py), [MIT license](https://github.com/sierra-research/tau-bench/blob/59a200c6d575d595120f1cb70fea53cef0632f6b/LICENSE). Its current repository is later than the original paper; a reproduction would need to pin the matching task definitions. No tasks, user simulator, prompts or grading code are imported.

**Cogitide transfer — inference/design.** Make invalid states difficult to express: a continuation carries a snapshot, an edit carries the observed hash, a remembered observation carries source events, and a running command cannot be reported as completed. Test policy adherence and resulting state, not just well-formed tool JSON. Reuse `IAgentTool`, current JSON contracts and validation. An evidence-view API should identify which information is historical, current, omitted or unverified without exposing implementation machinery to ordinary users.

**Cost and failure case.** A larger schema can burden model selection; a wrapper can increase tokens without improving decisions. An LLM user simulator adds cost and another stochastic source of error. Passing a single scripted fixture says little about repeated live-model reliability. A narrower interface may reject a legitimate unusual task, so rejection rate is a measured cost.

**Controlled experiment.** Start with generated local tasks and deterministic malicious/stale/ambiguous inputs. Later, only with an explicitly authorized model budget, compare identical task/model settings over repeated trials, checking final diffs/tests, approval denials, invalid-call recovery and intervention rates. Report all stopped and failed trials. Do not use final-test feedback to select the winning interface.

**Decision:** typed evidence and failure fixtures now; defer live user-simulator benchmarking and automatic schema optimization.

## 7. Direct evidence about coding-tool architecture

**Evidence.** Xu et al., *The Devil Is in the Interface: Evaluating How Tool Architecture Shapes Coding Agent Behavior*, **arXiv:2608.11386v1, 11 August 2026**, is a preprint studying **11,700 trajectories, six tool architectures and three actors**. It compares structured low-level operations, natural-language search, Python/CodeAct-style execution and lightweight reasoning scaffolds while keeping available information/actions similar. The authors report differences in consistency, exploration and interaction efficiency; lightweight text scaffolds have limited effects in their setup. These are coding-agent experiments, closer to this product than the preceding cross-domain transfers, but not independent Cogitide replication. No conference or presentation status is asserted. [Versioned primary paper](https://arxiv.org/abs/2608.11386v1), [Microsoft Research record](https://www.microsoft.com/en-us/research/publication/the-devil-is-in-the-interface-evaluating-how-tool-architecture-shapes-coding-agent-behavior/).

**Verified implementation reference.** The paper links author code/data at **`XZ-X/tool-arch-study@fd3fbbe111f19afe156942338db2f79dfec4185d`**. Its [implementation guide](https://github.com/XZ-X/tool-arch-study/blob/fd3fbbe111f19afe156942338db2f79dfec4185d/tool-interfaces.md) maps all six architectures to source. Natural-language search invokes a search subagent; Python actions combine operations through executable code. The [Trae-derived agents' license](https://github.com/XZ-X/tool-arch-study/blob/fd3fbbe111f19afe156942338db2f79dfec4185d/agent_src/trae-agent/LICENSE) and [Python agent's license](https://github.com/XZ-X/tool-arch-study/blob/fd3fbbe111f19afe156942338db2f79dfec4185d/agent_src/simple-agent/LICENSE) retain MIT terms and upstream attribution. Code, prompts, trajectories and data are not imported by this review.

**Cogitide transfer — inference/design.** Treat interface structure as a separate experimental variable from capability. Keep filesystem access, available source information, approvals, model, task set and total budgets fixed; vary only how observations can be requested or projected. The source-bound evidence views below are a narrower experiment than a second CodeAct runtime or an additional search agent. They should be judged on useful information, repeated reliability and total task cost, not merely tool-call count.

**Cost, negative cases and controlled experiment.** A search subagent adds model calls and may return noisy files. Executable composition complicates approval and outcome attribution. Compare proposed views/fusion against existing structured tools, useful `contextLines`, and **native same-response multi-call** support, with identical information and resource caps. Include tasks where those existing tools already suffice. Measure all actor/subagent tokens and work, final correctness, repeated failures and interventions. The abstract's percentage reductions are not forecasts or product results; fewer steps alone is not a win if work, risk or latency grows.

**Decision:** use this paper to design fair interface ablations now; defer production search subagents, a separate code-execution interface and live-model trials until their need and explicit evaluation budget are established.

## Two small prototypes with a distinct purpose

These are reviewable designs, not claims that production tools were added in this research pass.

### A. Source-bound views of persisted observations

Extend the existing `inspect_evidence` with a small fixed set of views over a chosen `eventSequence`, for example an original result's selected match page, diagnostic page, or read-content line range. Do not introduce SQL, arbitrary evaluation, a new document store, or another search parser. Reuse the original event, its event/result hashes, `System.Text.Json`, current output limits and source-specific contract validation.

Each response identifies the source session/run/event, original result hash, view parameters, omitted records and historical/current-state status. A projection cannot rewrite or overwrite the source event. Cross-session/account identifiers are rejected. Unsupported tool envelopes fall back to existing bounded original-detail recall. The concrete missing capability is retrieving a useful later part of a large persisted observation without reexecuting a tool or repeatedly presenting the same leading excerpt.

Compare against **current `inspect_evidence(eventSequence)`**, current context snippets, and a normal source reread. Measure useful source spans recovered per returned character, extra calls, parsing time and false freshness claims. For small observations this extension may be pure overhead. An improvement in observation efficiency is not automatically better coding success.

### B. Differential recheck of file-backed evidence

A narrow read-only recheck accepts a bounded list of existing session evidence references for known file-backed observations, initially `read_file` and `inspect_symbols`. Resolve references through the existing evidence reader; obtain the original path/hash from the validated original result, rather than trusting caller-supplied raw paths. Reuse the normal path/project-rule boundary and bounded full-file hashing. Return one explicit record per reference: `unchanged`, `changed`, `missing`, `unreadable`, `policy_changed`, or `unverified`, with the original hash, any observed current hash and source event.

The operation can reduce **returned context and repeated model reading**, especially when many previously inspected files remain unchanged. It does **not** inherently reduce disk reads: verifying an unchanged file still hashes its complete current content. It must not certify all source files in a semantic compilation from one file's hash, restore an old command's success, or mark an entire evidence graph current. Ordinary edit hashes still protect later mutations, since an external writer can modify a file after recheck.

Use the existing same-response multi-`read_file` baseline and compare exactly the same files under the same byte budget. Include 100% changed workloads, tiny files, unreadable files and changes during hashing; these can reverse the benefit. Keep original and recheck events so a stale-to-current claim remains independently inspectable. No private source content cache or new persistence layer is required.

## Evaluation plan and adoption gates

1. **Freeze contracts and workloads.** Record the exact source revision, .NET/Roslyn versions, fixture seeds, file count/size distributions, ignored and nested-project layouts, output limits and expected relevant source spans. Use small, medium and budget-bound cases, Unicode/long lines, ambiguous names, adversarial regex, external modification and cancellation.
2. **Measure actual local work.** Report elapsed distributions, managed allocations, retained/peak memory where available, file/byte/hash work, result characters, exact source coverage and cancellation waste. Separate cold construction from warm repeated use. BenchmarkDotNet iteration percentiles are not production request tail latency.
3. **Compare strong existing behavior.** Include search with maximal useful existing context and native same-response multi-call reads. Add projection, recheck and fusion independently before testing combinations. Do not attribute runtime metadata caching gains to an agent-planning mechanism.
4. **Check invariants before speed.** A changed same-length file, stale continuation, ignored or escaping path, approval denial, steering update, interrupted action or cleanup failure must never become an unqualified success. Keep complete negative outcomes and witness events.
5. **Separate deterministic and model evidence.** Generated local fixtures establish contracts and resource effects. Scripted API latency can isolate round-trip mechanics only if labeled synthetic. Live task solve rate, billable tokens, intervention and cost require a later explicitly authorized budget, fixed model/settings, development/selection/final splits and paired repeated trials. No such spending is authorized or performed by this document.
6. **Adopt only the demonstrated slice.** A candidate must preserve correctness/coverage and improve a named metric on the target workload without an unacceptable regression elsewhere. Report negative workloads and uncertainty. Keep research options explicit; no automatic budget growth, candidate activation or final-test selection.

The practical order is: profile the existing .NET path and retain the rejected metadata-cache measurements as negative evidence; test observation projection/recheck on generated histories; consider bounded fusion only where existing context and multi-call behavior leave a measured gap. Do not reintroduce the rejected cache with weaker identity checks to obtain a favorable number. Full learned scheduling, incremental source graphs, generic tool-plan languages and new transactional infrastructure remain deferred.

## 中文入口

本文从数据库查询计划、增量计算、操作系统调度与计算信息价值、事务历史验证、编译式工具依赖、工具接口可靠性六个方向核对原始论文及固定版本代码，并补充 2026 年直接比较编码工具架构的预印本。面向 Cogitide 的迁移不宣称学术创新或已获得论文收益；公共元数据缓存已因实测延迟回退而拒绝并撤回，不能改用弱时间戳缓存规避结果。优先考虑复用现有事件来源的按需投影视图、文件证据差异复核；对照必须包含现有搜索上下文和同轮多工具调用。差异复核仍需完整哈希，不能冒称节省磁盘读取。本文研究子任务没有运行 SDK、付费评测或自动激活候选。
