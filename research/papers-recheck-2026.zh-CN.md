[English](papers-recheck-2026.md) | **简体中文**

# 2026 论文复核：普通 API coding agent 的可用机制

**状态阅读说明：** 第 1–7 节保留本次资料复核阶段的判断、数字和当时实施状态；同日后续工程推进见 [第 8 节](#8-同日后续工程状态补充) 与 [当前研究实现说明](../docs/research-implementation.md)。历史记录中的“尚未实现”“先复用 Python”等建议不代表当前代码状态。公开产品名现为 Cogitide，原文中的 Cogito 是当时的开发代号。

**发布前证据纠正（同日再次核查）：** DCI 的正式题名是 [Beyond Semantic Similarity: Rethinking Retrieval for Agentic Search via Direct Corpus Interaction](https://arxiv.org/abs/2605.05242v1)，可在 [NeurIPS 2026 官方 Downloads](https://neurips.cc/Downloads/2026) 查到；但本次取回的 [官方事件 JSON](https://neurips.cc/static/virtual/data/neurips-2026-orals-posters.json) 共 9,278 条、`next=null`，没有找到原记录的 `155649`、`42dIxEhArH` 或论文题名。原活动页未能取回，OpenReview 要求浏览器验证。因此下面关于 **DCI Spotlight 的早期断言当前不能再次确认，应标为待复核**，不能将其作为当前已核实标签。此差异不等于论文未录用，也不改变 DCI 机制的研究价值。其余本项目采用论文的 GEPA/Gaia2/Speculative Actions Oral、ACE/RepoMem Poster、ACON regular、SkillOpt Spotlight 与此次官方事件字段吻合。

复核日期：**2026-10-30，Asia/Shanghai**。范围是 Cogito 现有统一 agent 内核上的搜索/分页读取、补丁与测试、后台进程、自动摘要、长任务恢复、项目规则和经验复用。只做资料复核与实验设计；没有运行训练、付费评测或论文复现。本文更新 [原论文清单](papers.md) 的证据等级，不把论文结果写成 Cogito 的实测收益。

**建议顺序：先做好可定位、可重读、可核验的工具与事件；然后小规模比较摘要和增量经验；最后离线优化技能。** 新增的 DCI 与 SkillOpt 都已核实为 NeurIPS 2026 Spotlight，且不要求更新基础模型权重。它们值得试验，但不是替换整个运行时的理由。当前没有任何一篇已被我们在 Cogito 上复现到足以宣称“充分验证”的程度。

## 1. 官方展示等级与年度：这次查到了什么

展示等级读取会议自己的公开事件数据，按 `paper_url`/论文身份合并 Poster 和 Oral 记录，检查 `decision` **以及** `eventtype`。不能用 URL 含 `/poster/` 推断没有 Oral，也不能只看作者 README。以下三个直接来源均于本次取回并检查 `results`：

- [ICLR 2026 官方事件 JSON](https://iclr.cc/static/virtual/data/iclr-2026-orals-posters.json)。
- [ICML 2026 官方事件 JSON](https://icml.cc/static/virtual/data/icml-2026-orals-posters.json)。
- [NeurIPS 2026 官方事件 JSON](https://neurips.cc/static/virtual/data/neurips-2026-orals-posters.json)。

OpenReview 部分网页要求浏览器验证，本轮没有绕过；会议 JSON 中的 `decision` 足以核验会议状态。论文内容则另读作者原稿、正式 proceedings、作者代码。JSON 包含重复活动和非论文活动，不能把事件总数直接当录用论文总数。

| 工作 | 首发年份 / 会议年度 | 本次官方核验结果与定位信息 |
| --- | --- | --- |
| GEPA | 2025 / ICLR 2026 | `Accept (Oral)`；[Oral 活动 10009494](https://iclr.cc/virtual/2026/oral/10009494)，论文 [RQm2KQTM5r](https://openreview.net/forum?id=RQm2KQTM5r) |
| ACE | 2025 / ICLR 2026 | `Accept (Poster)`；[10008343](https://iclr.cc/virtual/2026/poster/10008343)，论文 [eC4ygDs02R](https://openreview.net/forum?id=eC4ygDs02R)。不是本轮的 Oral/Spotlight |
| Improving Code Localization with Repository Memory / RepoMem | 2025 / ICLR 2026 | `Accept (Poster)`；[10011159](https://iclr.cc/virtual/2026/poster/10011159)，论文 [8yjWLJy2eX](https://openreview.net/forum?id=8yjWLJy2eX) |
| Gaia2 | 2026 / ICLR 2026 | `Accept (Oral)`；[10011091](https://iclr.cc/virtual/2026/poster/10011091)，论文 [9gw03JpKK4](https://openreview.net/forum?id=9gw03JpKK4)。ARE 的前身稿是 2025 年，不能混为同一版 |
| Speculative Actions | 2025 / ICLR 2026 | `Accept (Oral)`；[10009726](https://iclr.cc/virtual/2026/poster/10009726)，论文 [P0GOk5wslg](https://openreview.net/forum?id=P0GOk5wslg)。会议题名结尾为 “Faster AI Agents”，所读 arXiv v2 为 “Faster Agentic Systems” |
| ACON | 2025 / ICML 2026 | `Accept (regular)`；[66270](https://icml.cc/virtual/2026/poster/66270)，论文 [5EmOOLtH5P](https://openreview.net/forum?id=5EmOOLtH5P)。另有 ICLR workshop 版本，不能当成 ICLR 主会 |
| SkillOpt | 2026 / NeurIPS 2026 | `Accept (spotlight)`；[151264](https://neurips.cc/virtual/2026/poster/151264)，论文 [bgUar0JP5t](https://openreview.net/forum?id=bgUar0JP5t) |
| Direct Corpus Interaction / DCI | 2026 / NeurIPS 2026 | `Accept (spotlight)`；[155649](https://neurips.cc/virtual/2026/poster/155649)，论文 [42dIxEhArH](https://openreview.net/forum?id=42dIxEhArH) |

**NeurIPS 2026 不能再写成“还没开会所以录用未知”。** 官方 [日期页](https://neurips.cc/Conferences/2026/Dates) 显示主会在 12 月，本次已经读到上述录用和展示分级；准确措辞是“已接受为 Spotlight，会议尚未举行”。这也不意味着最终 proceedings、会后复现和实际报告均已完成。

额外纠正：HGM 的官方 ICLR 记录 `10009359` 是 Oral。daVinci-Dev 的 ICML Poster 记录 `63099` 虽仍写 `Accept (spotlight)`，但存在同论文的正式 [Oral 活动 71032](https://icml.cc/virtual/2026/oral/71032)，因此不能仅据 Poster 行将其降为 Spotlight。JitRL 的 [61517](https://icml.cc/virtual/2026/poster/61517) 为 Spotlight；这解决的是展示级别，不改变其 logits 接口不适合当前托管 API 的限制。

## 2. 证据分级：录用、开源和复现分别判断

- **V：会议身份已核实。** 上表所有条目均达到；不代表效果可迁移。
- **E1：作者原始实验。** 有明示任务、模型、基线和指标；逐项核查消融、重复运行与成本。
- **E2：独立复现了原始主要结论。** 需要他人重跑相近协议、有可核对结果；仓库 star、集成、改编教程和作者新增实验都不算。
- **E3：Cogito 留出任务上的复现。** 同模型、工具、预算、初始 revision，配对多次运行并记录失败。**本轮八篇均未达到 E3。**

本次未核实八篇任一篇已经在普通 API **coding 修复任务**上获得独立团队对其主要收益的完整复现。下面明确列为未知，而不是断言世上不存在复现。ACE 有独立改编实验线索，但 [AgentDescent 的 FiNER 实验](https://birfy.github.io/agentdescent/algo-ace/) 修改了整理/验收机制且不跑完整 AppWorld；只能算部分机制实验，不能升级成原论文整体 E2。GEPA 在后续研究中作为基线，也不等于复现其全部主结论。

SkillOpt 的 [作者后续消融说明](https://microsoft.github.io/SkillOpt/blog/gating-reflection-safe-updates/) 还明确指出：499-run 扩展消融的原始逐次产物没有公开，部分比较混合改变多个控制项；`max(best-on-val, final)` 是看过测试后的探索性 oracle 指标，不能当部署选择规则。当前 `main` 的 slow-update 默认行为也不完全等于论文 fully-gated 方案。后续若改编，必须显式固定这些选项；既不能把“有很多 runs”当独立复现，也不能照搬后续 blog 的最高分作为主要证据。

## 3. 八篇候选的实验与适配矩阵

以下数字仅描述所链接版本的原始评测。置信区间、重复优化和独立复现未核实的地方保留未知；不把固定 seed 当作多次重复。

| 工作与原稿 | 原始任务、基线、消融 | 重复与证据限制 | 普通 API、成本与采用判断 |
| --- | --- | --- | --- |
| **RepoMem**：[v2 §4、附录](https://arxiv.org/html/2510.01003v2) | SWE-bench Verified 500 题、Live 筛选后 130 题；GPT-4o；对照 LocAgent、Agentless、CodeRankEmbed；分离 episodic/semantic memory，并比较检索器。Verified 文件 Acc@5 从 71.6 到 76.5 | 未核实主表多 seed；主要指标是定位。历史稀薄的 “others” 组反而下降 13.1 点；不能只摘平均收益 | 不需训练；按任务 base commit 截断历史。构建与摘要有离线成本，平均 API 开销并非必降。**P1：受预算的历史检索；不先上图/向量数据库** |
| **ACE**：[v2 §4](https://arxiv.org/html/2510.04618v2) | AppWorld、FiNER、Formula；对照 ReAct/ICL/MIPROv2/GEPA/Dynamic Cheatsheet；有 reflector、multi-epoch、warmup 消融 | 主表多次独立优化/置信区间未核实；在线序列使用同一打乱顺序。反馈不可靠会污染经验；不是 coding 修复评测 | 普通 API；模型生成 delta、整理 playbook。额外反思/整理调用及 playbook token 必须计费。**P1：项目内小条目；保留确定性合并、来源、撤销，不复制全套每步多模型流水线** |
| **ACON**：[v3](https://arxiv.org/html/2510.00615v3) | AppWorld、OfficeBench、8-objective QA，另有 WebVoyager 留出补充；比较压缩策略；对照完整上下文与压缩后失败轨迹，优化压缩指南；有阈值、优化器、历史/观察压缩消融 | 固定快照/seed 不等于多 seed；领域与 C# 编码有差距。论文区分峰值 context token 与真实 API 费用 | 指南优化无需权重训练；小 compressor 蒸馏是另一条需训练分支。历史重写可能损害缓存、增加费用。**P1：先改善摘要验收和日志分页，再离线优化摘要提示；不声称“压缩必省钱”** |
| **GEPA**：[v2 §4、E–I](https://arxiv.org/html/2507.19457v2) | 六类任务包括 HotpotQA/HoVer/IFBench/PUPA/数学；Qwen3-8B 与 GPT-4.1-mini；GRPO/MIPROv2/TextGrad/Trace；有 Pareto/merge 消融和留出集。代码实验含 35 个 KernelBench 子任务 | 数学测试有重复样本，但未核实全套独立优化 seed 方差；kernel 结果是早期补充，不能当仓库修复结果 | 不更新权重，但需要评测、反思和优化预算；rollout 数不等于 token/费用。**P2：离线技能候选优化，先复用 Python 研究工具通过 JSONL 接 .NET；不移植整个优化器进桌面** |
| **SkillOpt**：[v1 §4、附录 C](https://arxiv.org/html/2605.23904v1) | 六主任务涵盖 QA、表格、文档、数学、ALFWorld；七模型及 direct/Codex/Claude Code harness；对照人工/一次生成技能、GEPA/TextGrad/Trace2Skill/EvoSkill；有编辑预算、gate、teacher、迁移消融 | 固定 train/selection/test 划分；未核实多次独立优化方差。使用 Codex harness **不等于**做过 SWE-bench；“52 cells”不是 52 次独立复现 | 模型冻结，输出紧凑 skill；部署无额外 optimizer 调用，离线预算却可能很大。**P2：作为 GEPA 的竞争臂，受限 add/delete/replace + 独立最终测试；不照搬重预算默认配置** |
| **DCI**：[v1 §4](https://arxiv.org/html/2605.05242v1) | BrowseComp-Plus、知识 QA、BRIGHT/BEIR 子集；稀疏/稠密/重排基线；有同模型工具、context 策略与语料规模消融。100 题子集 read+grep 61%，稠密检索 45% | 主表多次随机重复未核实；部分主比较模型/harness 不同，应优先看匹配消融。不是直接的代码修复评测 | 普通 API，无须 embeddings；到 400K 文档出现明显性能/成本退化。**P0：精确检索、分页读、命中前后文、回读原始证据；不推广成所有规模都不要索引** |
| **Gaia2**：[v1 §5、B.3](https://arxiv.org/html/2602.11964v1) | 800 动态场景，多个模型统一 ReAct；按时间、噪声、适应等能力分组；每场景三次；有 parallel-tool-calling 和时间因素分析 | 模拟应用与模型 verifier；不是生产桌面可靠性证明。并行改善延迟/用量，对完成率变化较小 | 评测可用 API，无需训练。**P0：迁移为后台完成事件、取消、外部文件变更和过期观察夹具；不把 ARE 全套应用搬入产品** |
| **Speculative Actions**：[v2 §3–5](https://arxiv.org/html/2510.04371v2) | chess、零售、HotpotQA，以及明确标作 lossy 的 OS 调优；对照串行和不同预测分支/策略；分析预测命中与成本/延迟 | 未核实整套实验的独立重复与方差；“lossless”依赖匹配检查和无可见副作用/可回滚假设，OS 结果不能支持无损结论 | 普通 API，但预测和未命中分支付费；可能增加限流。**P3：先独立只读并发；后测最多一条预取。不预测执行 patch、安装、Git 写入或测试** |

矩阵中的 P0–P3 是本项目判断，并非论文排名。原始分数、历史价格与当前供应商报价不可混用。八篇的目标机制均可在冻结模型下研究；GEPA/SkillOpt 的“训练”主要指文本搜索，仍然消耗调用费用。ACON 的蒸馏、MemAgent/AgentFlow 的 RL、daVinci-Dev 的 mid-training 不属于这一免费接口能力。

## 4. 作者代码、许可证与复用方式

| 工作 | 作者实现与当前可核对许可证 | 是否需要开放权重 / 本轮复用方式 |
| --- | --- | --- |
| RepoMem | 原稿和 [Microsoft Research 论文页](https://www.microsoft.com/en-us/research/publication/improving-code-localization-with-repository-memory/) 未找到能核实归属与许可证的作者实现；同名 RepoMem 项目不能代替 | 不要求开放权重。先独立实现 git 历史检索，不声称已移植作者代码 |
| ACE | [ace-agent/ace](https://github.com/ace-agent/ace)、[Apache-2.0](https://github.com/ace-agent/ace/blob/main/LICENSE.txt) | API 可用；AppWorld 子模块及数据需另核许可证。借鉴 playbook/delta，避免成为生产外部服务 |
| ACON | [microsoft/acon](https://github.com/microsoft/acon)、[MIT](https://github.com/microsoft/acon/blob/main/LICENSE) | API 指南优化不要求权重；蒸馏需要训练。复用研究评测方式与压缩失败诊断 |
| GEPA | [gepa-ai/gepa](https://github.com/gepa-ai/gepa)、[MIT](https://github.com/gepa-ai/gepa/blob/main/LICENSE) | 不要求开放权重；离线调用研究工具，导出版本化技能 |
| SkillOpt | [microsoft/SkillOpt](https://github.com/microsoft/SkillOpt)、[MIT](https://github.com/microsoft/SkillOpt/blob/main/LICENSE) | 不要求开放权重。作者 [复现指南](https://microsoft.github.io/SkillOpt/docs/guideline.html) 明确区分论文 runner 与后来的 SkillOpt-Sleep preview；不能拿 preview 当论文已验证能力 |
| DCI | [DCI-Agent-Lite](https://github.com/DCI-Agent/DCI-Agent-Lite)、[MIT](https://github.com/DCI-Agent/DCI-Agent-Lite/blob/main/LICENSE) | 不要求开放权重；优先改编工具返回契约/context 管理，不引入作者完整 CLI/harness |
| Gaia2 | [ARE](https://github.com/facebookresearch/meta-agents-research-environments)、[MIT](https://github.com/facebookresearch/meta-agents-research-environments/blob/main/LICENSE) | baseline 可 API；自托管 verifier/模型会另需算力。用独立评测服务或借鉴事件夹具，不加入 GUI 运行时 |
| Speculative Actions | [作者仓库](https://github.com/naimengye/speculative-action)、[Apache-2.0](https://github.com/naimengye/speculative-action/blob/main/LICENSE) | 不要求开放权重；只读调度/缓存概念适合 BCL Task/CancellationToken；不复制 OS lossy 调节行为 |

这些是 **2026-10-30 的复用候选核查**，本轮没有把上述研究代码/提示词并入产品。若后续复制或按 C# 翻译，必须固定实际使用的 commit、文件路径、修改说明，保留对应 LICENSE/NOTICE；主仓库许可证不能自动覆盖数据、子模块、模型权重。网络、JSON、取消、并发、持久化继续复用项目已有 .NET 组件，不重写研究仓库的 Python/TS 基础设施。

## 5. 其他 2026 Spotlight 候选：有价值，但不要扩张内核

| 工作 | 官方状态 | 取舍 |
| --- | --- | --- |
| **MEnvAgent** | ICML 2026 Spotlight，[64521](https://icml.cc/virtual/2026/poster/64521)；[论文](https://arxiv.org/html/2601.22859v2)、[Apache-2.0 代码](https://github.com/ernie-research/MEnvAgent) | 1,000 环境任务/10 语言，有环境复用消融；适合后续建立可重放修复评测、缓存构建环境。环境构建 F2P 不是 coding solve rate；不照搬多 agent 配置，更不启动其训练分支 |
| **MemoryBench** | ICML 2026 Spotlight，[64918](https://icml.cc/virtual/2026/poster/64918)；[论文](https://arxiv.org/abs/2510.17281)、[代码](https://github.com/THUIR/MemoryBench) | 评估反馈学习，比只问历史问答更贴近 procedure。仍偏非编码任务；作者仓库有后续评测分支和上游多许可证，需固定原论文版本，不混跑后宣布复现 |
| **ThunderAgent** | ICML 2026 Spotlight，[62040](https://icml.cc/virtual/2026/poster/62040)；[论文](https://arxiv.org/abs/2602.13692) | 依赖自托管推理调度/KV cache 的收益不是普通托管 API 可控制的能力。仅借鉴工具环境异步准备，暂不进入产品路线 |
| **JitRL** | ICML 2026 Spotlight，[61517](https://icml.cc/virtual/2026/poster/61517)；[正式论文](https://proceedings.mlr.press/v306/li26cv.html) | 无梯度不等于无模型接口要求；logits 调制不适合当前 provider API，暂不移植 |
| **Self-Programmed Execution / Spell** | NeurIPS 2026 Oral，官方 JSON 中 `155631`；[论文](https://arxiv.org/abs/2605.06898)、[作者代码](https://github.com/lukejoconnor/spell) | 模型编写自身编排程序是更大的架构实验。当前恢复、取消、副作用边界应保持可核对，不能仅因 Oral 就换成自修改解释器 |

[AMA-Bench](https://proceedings.mlr.press/v306/zhao26bs.html) 的 ICML 官方记录 `65112` 为 regular；其 ICLR workshop Oral 不等于 ICLR 主会 Oral。它对 agent 轨迹而非单纯聊天记忆的评测有用，[作者代码](https://github.com/AMA-Bench/AMA-Bench/) 的 construction/retrieval 两阶段接口可作对照；但检索 QA 分数仍不是恢复后成功修复代码的指标。不先移植 AMA-Agent 的因果图。

## 6. 面向现有工程的四个小切片

下面是工程推断，保留 [核心记忆设计](core-memory-design.md) 的来源与事务约束，不要求再造一个研究服务层。

1. **搜索和证据访问（P0，DCI + RepoMem）。** 搜索结果返回路径、行号、有限 snippet、是否截断、续页标记；读取支持范围、文件版本及稳定回读。原始工具输出保留引用，摘要仅是入口。查询失败后能缩小/改变查询，不能让 top-k 截断成为不可恢复的信息丢失。先比较现有 rg/路径搜索；历史检索只读当前 base revision 之前的 commit，禁用目标修复泄漏。
2. **后台与长任务连续性（P0，Gaia2）。** 后台启动返回 operation ID；完成、失败、超时、取消、用户 steering 在同会话有序记录。恢复重新核对进程/文件状态，不重放未知副作用。patch 前核对源版本，测试证据绑定被测 revision；背景测试完成不自动证明后来修改也通过。这属于确定性正确性，先用无模型夹具验收。
3. **摘要和项目经验（P1，ACON + ACE）。** 摘要保留用户原约束、进行中操作和证据引用；通过分页重读恢复细节。经验 delta 先存 Candidate，附项目/路径、适用条件、来源、版本；只在可核对结果后更新，遇到反例降级。不要每步强制 reflector/curator 调用；对低信息日志先确定性裁剪/分页。经验不得覆盖 AGENTS.md 或提高工具权限。
4. **技能优化（P2，GEPA 与 SkillOpt 二选一验证）。** 两者共用同一轨迹/评分 JSONL 协议，不各建一套运行时。候选仅修改一个受预算的 skill；优化集、选择集、最终测试集隔离；当前用户任务固定技能版本。部署只加载已选文本，不在后台默认无限演化。先比较简单人工/单次失败总结基线，能获益再增加搜索复杂度。

P3 推测执行等这些切片有实测瓶颈之后再做：先显式并行独立只读调用，测时间和限流，再允许最多一条只读预测分支；按输入、内容版本、provider/account 和取消令牌隔离缓存。

## 7. 固定预算的最小评测协议（尚未执行）

先把“状态机没出错”和“模型更会解决问题”分开。第一阶段使用现有测试工程添加/复用确定性夹具，不产生模型费用：大输出分页重读、外部写入导致旧证据失效、压缩与 steering 竞态、补丁版本冲突、后台完成晚于取消、进程重启不重放工具。这些通过只证明实现正确。

后续真实模型实验采用以下 **提案上限**，不是已启动任务或已获费用授权：

| 阶段 | 固定范围 | 上限和停止条件 | 能回答的问题 |
| --- | --- | --- | --- |
| 开发冒烟 | 8 个自有小仓库任务，四类各两题：定位修复、长日志、恢复/steering、相似任务迁移；2 臂 × 1 次 | 16 episodes；每题最多 30 轮、10 分钟；全阶段 1M 输入 token、100K 输出 token 或 US$10，任一先到即停 | 工具/评分是否可运行；不做提升显著性声明 |
| 首轮留出 | 24 题，按仓库和问题族与开发集隔离；固定模型快照；2 臂 × 3 次 | 144 episodes；全阶段 5M 输入、500K 输出或 US$50，任一先到即停。预算不足保留不完整结果，不补跑挑选成功样本 | 同预算下完整摘要与一个增量机制的配对差异；小样本仅作筛选 |
| 离线技能优化（单独阶段） | 在开发任务上最多 8 个候选、每候选最多 4 题；选择集固定，最终测试集不参与优化 | 最多 32 候选评测；优化器最多 16 次调用；1M 输入、100K 输出或 US$10，先到即停；优化与验证全部计入 | GEPA/SkillOpt 式搜索是否值得超过简单固定技能；不追逐论文重预算结果 |

预算计入摘要、反思、重试、选择评测及未命中推测，按实际 provider usage 和运行前确认的当前价格记录；某 API 无费用单价时同时以 token 和硬次数限额停机，不虚构费用。每次只改变一个机制，先 A=现有工具/摘要，B=一个新切片；不一次把 ACE+GEPA+DCI+speculation 全开再归因。历史缓存与环境缓存的冷热条件匹配。

主指标是冻结验收测试下的修复成功率、用户约束违反、使用过期证据/副作用重放；副指标为失败类别、无效读取、恢复后重复劳动、各类 token、压缩/索引开销、每成功任务费用及 p50/p95。保存精确模型、配置、初始 commit、工具版本、任务顺序和全部失败。报告配对 bootstrap 区间或适合样本的配对检验；24 题的 p95/置信区间都可能很不稳定，不声称已经有足够统计功效。

发布研究增强前，先要求没有确定性契约回归；能力提升的区间若仍跨零，或成本超出预设可接受边界，则继续保留成熟基线，新机制仅作为可关闭实验。最终验收不能由生成该经验/技能的模型单独打分，也不能为了“通过”修改测试或读取隐藏参考补丁。

## 8. 同日后续工程状态补充

以下记录 **2026-10-30 资料复核之后的工程实现**，用于与上面的历史建议区分。具体配置、入口、验证与限制以 [当前研究实现说明](../docs/research-implementation.md) 和各模块 README 为准；这里不改写历史测试数字，也不把实现完成升级成 E2/E3 研究效果复现。

| 机制来源 | 当前落地范围 | 尚未声称或实现的部分 |
| --- | --- | --- |
| ACE / APC | [证据与经验模块](../src/Workbench.Core/Memory/README.md) 保存增量经验和有条件步骤候选，召回核对真实工具事件与已报告文件版本。 | 未复现完整 ACE 多角色学习流水线、APC 的全部计划适配算法；来源检查不证明自然语言经验必然正确。 |
| ACON | [Context](../src/Workbench.Core/Context/README.md) 增加可选的版本化摘要指南、观察投影和独立来源附录；离线失败诊断提案与成对选择验收可执行。 | 未实现蒸馏、权重训练或论文完整双压缩器；上下文字符减少不等于费用降低。 |
| RepoMem / DCI | [RepositoryMemory](../src/Workbench.Tools/RepositoryMemory/README.md) 按任务冻结 Git 历史边界，检索提交、路径和共同修改线索；与现有搜索、分页读取和证据回读组合使用。 | 未实现 RepoMem 的学习检索器、LLM 功能摘要或 LocAgent 图；未移植 DCI 的整套 harness，也未证明大语料收益。 |
| GEPA | [SkillOptimization](../src/Workbench.Core/Research/SkillOptimization/README.md) 根据训练反馈提出技能修改，用逐任务赢家集合及支配检查保留互补候选，支持有界双父候选合并。 | 实现单技能模块的有限搜索，不是完整 compound-AI 优化器或作者 system-aware crossover。 |
| SkillOpt | 与 GEPA 共用 C# 执行、预算和产物协议；采用结构化失败反馈、受限编辑及选择集逐任务不退步的更新门控。 | 未实现 slow-update、meta-skill 或 SkillOpt-Sleep；门控更保守，不把它称为论文代码逐行复现。 |
| Speculative Actions | [ReadOnlyPrefetch](../src/Workbench.Core/Research/Speculation/ReadOnlyPrefetch.cs) 只预测一条 `read_file`，按精确请求、当前内容哈希和运行预算核对后复用。 | 不预测执行写文件、patch、安装或测试；无论文级无损/延迟收益证明。 |
| Gaia2 | 动态环境思想用于后台完成、取消、外部变更、过期观察和恢复的工程夹具；运行资源仍由统一内核管理。 | 未运行 Gaia2/ARE 的 800 场景基准，也没有搬入模拟应用或把这些夹具当桌面可靠性证明。 |

离线优化现在通过 [Host 研究入口](../src/Workbench.Host/README.md) 显式启动：`--optimize-skill` 或 `--optimize-compression` 配合配置、全新输出目录及 `--validate-only` / `--execute`。实际任务评测复用已有 AgentEngine、固定仓库 revision、允许的命令与冻结验收；优化不只重放预填分数。此处将第 3、4 节提出的 Python 研究工具接入路线收敛为共享 C# 实现，**没有把作者 Python 代码或提示词复制入产品**。正常聊天不会自动训练、追加调用预算或安装导出的候选。

运行内核的压缩指南、仓库记忆和只读预取均由显式 `ResearchOptions` 开关控制，默认关闭；普通聊天不自动启动离线优化。训练/选择/最终测试隔离，最终测试不能反向选择候选；取消、未知用量和预算不足保留失败或不完整状态。研究模块继续复用官方模型 SDK、Git CLI、SQLite 和 .NET/BCL，不引入第二套网络、进程或存储服务。

这些是论文机制的可执行切片以及本项目的来源、版本、生命周期和预算约束。**当前没有新增可归因的编码成功率、成本或延迟提升结论，也没有宣称完整复现任一论文。** 除本文开头说明的 DCI 待复核差异外，会议身份沿用第 1 节列出的官方来源；实现新增不增加 Oral/Spotlight 标签，APC 仅按其 [NeurIPS 2025 正式论文](https://proceedings.neurips.cc/paper_files/paper/2025/hash/9549f7d06700f0966d5f938f1d11022a-Abstract-Conference.html) 表述为主会工作。
