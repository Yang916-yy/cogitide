**English** | [简体中文](papers-recheck-2026.zh-CN.md)

# 2026 paper recheck: mechanisms usable through ordinary APIs

Historical primary source review **2026-10-30**, distinguishing acceptance/presentation, open source, functional implementation and independent effect reproduction. This summary preserves the original conclusions and numerical qualifications; it does not freshly verify changing conference/author pages or initiate the proposed budgets.

Official classifications recorded in the original: **GEPA, Gaia2 and Speculative Actions: ICLR 2026 Oral; ACE and RepoMem: ICLR 2026 Poster; ACON: ICML 2026 regular; SkillOpt and Direct Corpus Interaction: NeurIPS 2026 Spotlight**. First arXiv year differed from conference year for several papers. ACON's workshop version was not ICLR main conference; Gaia2 was not the same revision as its 2025 ARE predecessor. The retained official event/OpenReview links provide exact IDs.

RepoMem evaluated SWE-bench Verified 500 and filtered Live 130 with localization metrics/episodic versus semantic ablations. Verified file Acc@5 **71.6→76.5** was not repair solve rate; sparse history group dropped **13.1 points**, independent multi seed variation was not established and summaries cost money. Adopt bounded frozen ancestry search before graphs/vectors.

ACE evaluated AppWorld/FiNER/Formula and playbook/reflector/epoch/warmup ablations; it was not coding repair, feedback could contaminate entries and independent optimizer variance was unverified. ACON compared context/guideline/history/observation compression on AppWorld/OfficeBench/QA with supplemental WebVoyager; peak context differs from real cost and cache damage can reverse savings. Guideline optimization is API feasible, distillation requires training.

GEPA tested six broad tasks/Qwen3-8B/GPT-4.1-mini with Pareto/merge ablations and supplemental **35 KernelBench** subtasks, not repository bug repair. SkillOpt tested six tasks/seven models/harnesses, bounded edits/gates/teacher/transfer; Codex harness use was not SWE-bench and **52 cells** were not 52 independent replications. Both freeze weights but can require substantial offline rollout/reflection/evaluation usage. Unverified full independent optimization variance limits effect claims.

DCI compared sparse/dense/reranked retrieval on BrowseComp-Plus/QA/BRIGHT/BEIR with matched tool/context/scale ablations. A **100 question subset: read+grep 61%, dense 45%** did not prove coding gains; models/harnesses differed in some main comparisons and **400K documents** showed cost/performance degradation. Adopt exact search/pages/context/raw source reread, without claiming all scales need no index. Gaia2 used **800 simulated scenarios/three runs each** and dynamic noise/time analysis, not production desktop proof; tool parallelism mainly improved latency/usage with smaller success changes. Speculative Actions compared chess/retail/HotpotQA and explicitly lossy OS tuning; misses/branch predictions cost resources and losslessness requires match/reversibility.

Author implementations/licenses reviewed were ACE Apache 2.0; ACON/GEPA/SkillOpt/DCI/ARE MIT; Speculative Actions Apache 2.0. A trustworthy author RepoMem implementation/license was not identified. No author source/prompts were imported by this research round; future copies need fixed commits/files/differences/notices and separate data/model licenses. SkillOpt-Sleep preview was not the paper runner. BCL Task/CancellationToken/JSON/hash/SQLite/existing SDKs should replace unnecessary Python/TS infrastructure ports.

Additional recorded candidates were ICML Spotlight MEnvAgent/MemoryBench/ThunderAgent/JitRL and NeurIPS Oral Spell. Environment building F2P was not coding solve rate; feedback benchmarks/retrieval QA were not post recovery repair; self hosted KV scheduling/logits control did not exist in ordinary API clients. Spell self programmed orchestration was a larger experiment, not a reason to replace auditable cancellation/side effects. AMA-Bench was ICML regular, not upgraded by a workshop Oral.

Proposed small slices prioritized exact evidence/search and dynamic fault fixtures, conservative incremental memory/summary, independent offline skill search and finally one read only prefetch. **These budget proposals were not started or spending authorization:** development eight tasks/two arms/16 episodes, max thirty rounds/ten minutes each and **1M input/100K output tokens or US$10**; first holdout 24 isolated tasks/two arms/three repetitions/**144 episodes**, **5M/500K or US$50**; offline optimization max eight candidates/four tasks/**32 evaluations**, sixteen proposer calls and **1M/100K or US$10**. First reached limit stops; unfinished/failure outcomes stay in the report. All inference/summary/optimizer usage must count, with paired task analysis rather than cherry picked successes.

Same day engineering additions were separately recorded: ACE/APC sourced candidate procedures; ACON versioned guideline/source protected views and explicit paired comparisons; RepoMem/DCI frozen Git/cochange retrieval; limited single skill GEPA Pareto/merge; stricter SkillOpt nonregression; exact hash checked read only prefetch; Gaia2 inspired background/cancel/external change fixtures. Full learned graphs, distillation, complete author systems/crossover/Sleep, write speculation and the ARE benchmark were not claimed. Those are functional engineering slices, not independent paper effect or novelty proofs; current module READMEs and implementation records own later delivery results.

This English page summarizes the preserved source. [The full Chinese record](papers-recheck-2026.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](#8-同日后续工程状态补充)
- [Original reference 2](../docs/research-implementation.md)
- [Original reference 3](https://arxiv.org/abs/2605.05242v1)
- [Original reference 4](https://neurips.cc/Downloads/2026)
- [Original reference 5](https://neurips.cc/static/virtual/data/neurips-2026-orals-posters.json)
- [Original reference 6](papers.md)
- [Original reference 7](https://iclr.cc/static/virtual/data/iclr-2026-orals-posters.json)
- [Original reference 8](https://icml.cc/static/virtual/data/icml-2026-orals-posters.json)
- [Original reference 9](https://iclr.cc/virtual/2026/oral/10009494)
- [Original reference 10](https://openreview.net/forum?id=RQm2KQTM5r)
- [Original reference 11](https://iclr.cc/virtual/2026/poster/10008343)
- [Original reference 12](https://openreview.net/forum?id=eC4ygDs02R)
- [Original reference 13](https://iclr.cc/virtual/2026/poster/10011159)
- [Original reference 14](https://openreview.net/forum?id=8yjWLJy2eX)
- [Original reference 15](https://iclr.cc/virtual/2026/poster/10011091)
- [Original reference 16](https://openreview.net/forum?id=9gw03JpKK4)
- [Original reference 17](https://iclr.cc/virtual/2026/poster/10009726)
- [Original reference 18](https://openreview.net/forum?id=P0GOk5wslg)
- [Original reference 19](https://icml.cc/virtual/2026/poster/66270)
- [Original reference 20](https://openreview.net/forum?id=5EmOOLtH5P)
- [Original reference 21](https://neurips.cc/virtual/2026/poster/151264)
- [Original reference 22](https://openreview.net/forum?id=bgUar0JP5t)
- [Original reference 23](https://neurips.cc/virtual/2026/poster/155649)
- [Original reference 24](https://openreview.net/forum?id=42dIxEhArH)
- [Original reference 25](https://neurips.cc/Conferences/2026/Dates)
- [Original reference 26](https://icml.cc/virtual/2026/oral/71032)
- [Original reference 27](https://icml.cc/virtual/2026/poster/61517)
- [Original reference 28](https://birfy.github.io/agentdescent/algo-ace/)
- [Original reference 29](https://microsoft.github.io/SkillOpt/blog/gating-reflection-safe-updates/)
- [Original reference 30](https://arxiv.org/html/2510.01003v2)
- [Original reference 31](https://arxiv.org/html/2510.04618v2)
- [Original reference 32](https://arxiv.org/html/2510.00615v3)
- [Original reference 33](https://arxiv.org/html/2507.19457v2)
- [Original reference 34](https://arxiv.org/html/2605.23904v1)
- [Original reference 35](https://arxiv.org/html/2605.05242v1)
- [Original reference 36](https://arxiv.org/html/2602.11964v1)
- [Original reference 37](https://arxiv.org/html/2510.04371v2)
- [Original reference 38](https://www.microsoft.com/en-us/research/publication/improving-code-localization-with-repository-memory/)
- [Original reference 39](https://github.com/ace-agent/ace)
- [Original reference 40](https://github.com/ace-agent/ace/blob/main/LICENSE.txt)
- [Original reference 41](https://github.com/microsoft/acon)
- [Original reference 42](https://github.com/microsoft/acon/blob/main/LICENSE)
- [Original reference 43](https://github.com/gepa-ai/gepa)
- [Original reference 44](https://github.com/gepa-ai/gepa/blob/main/LICENSE)
- [Original reference 45](https://github.com/microsoft/SkillOpt)
- [Original reference 46](https://github.com/microsoft/SkillOpt/blob/main/LICENSE)
- [Original reference 47](https://microsoft.github.io/SkillOpt/docs/guideline.html)
- [Original reference 48](https://github.com/DCI-Agent/DCI-Agent-Lite)
- [Original reference 49](https://github.com/DCI-Agent/DCI-Agent-Lite/blob/main/LICENSE)
- [Original reference 50](https://github.com/facebookresearch/meta-agents-research-environments)
- [Original reference 51](https://github.com/facebookresearch/meta-agents-research-environments/blob/main/LICENSE)
- [Original reference 52](https://github.com/naimengye/speculative-action)
- [Original reference 53](https://github.com/naimengye/speculative-action/blob/main/LICENSE)
- [Original reference 54](https://icml.cc/virtual/2026/poster/64521)
- [Original reference 55](https://arxiv.org/html/2601.22859v2)
- [Original reference 56](https://github.com/ernie-research/MEnvAgent)
- [Original reference 57](https://icml.cc/virtual/2026/poster/64918)
- [Original reference 58](https://arxiv.org/abs/2510.17281)
- [Original reference 59](https://github.com/THUIR/MemoryBench)
- [Original reference 60](https://icml.cc/virtual/2026/poster/62040)
- [Original reference 61](https://arxiv.org/abs/2602.13692)
- [Original reference 62](https://proceedings.mlr.press/v306/li26cv.html)
- [Original reference 63](https://arxiv.org/abs/2605.06898)
- [Original reference 64](https://github.com/lukejoconnor/spell)
- [Original reference 65](https://proceedings.mlr.press/v306/zhao26bs.html)
- [Original reference 66](https://github.com/AMA-Bench/AMA-Bench/)
- [Original reference 67](core-memory-design.md)
- [Original reference 68](../src/Workbench.Core/Memory/README.md)
- [Original reference 69](../src/Workbench.Core/Context/README.md)
- [Original reference 70](../src/Workbench.Tools/RepositoryMemory/README.md)
- [Original reference 71](../src/Workbench.Core/Research/SkillOptimization/README.md)
- [Original reference 72](../src/Workbench.Core/Research/Speculation/ReadOnlyPrefetch.cs)
- [Original reference 73](../src/Workbench.Host/README.md)
- [Original reference 74](https://proceedings.neurips.cc/paper_files/paper/2025/hash/9549f7d06700f0966d5f938f1d11022a-Abstract-Conference.html)
