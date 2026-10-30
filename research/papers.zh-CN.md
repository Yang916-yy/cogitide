[English](papers.md) | **简体中文**

# 2026 顶会研究到 Coding Agent 的迁移备忘录

调研日期：2026-10-30（Asia/Shanghai）。最终范围：以 C# / .NET coding agent 为核心，提供 Windows、Linux、macOS 图形界面；work 只覆盖开发中高频的少量资料检索、文件与文档操作，避免引入通用桌面自治的复杂度。本轮是研究与路线设计，没有复现实验，也没有启动模型训练或付费评测。

**同日复核更新：** 展示等级、实验强度和实施优先级以 [2026 论文复核矩阵](papers-recheck-2026.md) 为准。本次已读取会议官方事件 JSON：GEPA/Gaia2/Speculative Actions 确认为 ICLR Oral，ACE/Repository Memory 为 Poster；新增 NeurIPS 2026 已录用 Spotlight 的 DCI、SkillOpt，区分会议尚未举行与录用已公布。下面保留首次调研记录，其中“只核实作者标注/未完成类型复核”反映当时证据，不能覆盖新矩阵；Oral、开源与独立复现分别判断。

**后续发布核查补充：** 上段有关 DCI Spotlight 的早期判断当前待复核：官方 Downloads 有论文题名，但再次取回的事件 JSON 未找到原活动/论文 ID；请以 [复核矩阵开头的证据纠正](papers-recheck-2026.md) 为准。已落地机制见该文第 8 节与 [当前研究实现说明](../docs/research-implementation.md)，不把本页的历史规划当作未完成清单。

## 结论

最值得先实现的研究方向是：**单 agent 的代码修改与验证闭环、受预算约束的仓库上下文、可回溯的长期工程任务状态，以及用留出任务优化 skills**。首版先把定位、编辑、构建/测试、diff 审阅、取消与恢复做扎实。没有必要把首版做成多 agent 辩论框架，也没有证据表明把 planner、executor、verifier 分成多个 LLM 调用就能自动获得论文中的训练收益。

当前优先级是 GEPA 的离线技能优化、仓库记忆与测试反馈；Speculative Actions 只研究受限只读并发。Gaia2 的方法迁移到 session 异步事件、工具状态和写操作核对。OpenApps 与全桌面 computer use 作为以后扩展的资料保留，不构成首版必做项；跨平台 GUI 本身不要求具备操作任意桌面应用的能力。

HGM 和 daVinci-Dev 提供了今年 coding 研究的补充视角，但分别涉及大规模 harness 搜索和模型训练，不应因 Oral 标签而抬升为首版依赖。以下方案均区分可直接借鉴的机制与需要重新验证的收益。

## 证据标准与年度边界

- “2026”指会议年度，预印本可能首次发表于 2025 年。下表将两者分开，不能把 arXiv 首发年份当会议年份。
- presentation 类型由论文作者的 arXiv 注记、官方项目、作者简历或会议录播页面核对；内容由原论文、proceedings 或作者代码核对。作者个人页也是一手来源，但其权威性不同于会议录用数据库，故保留具体证据链接。
- 本次 OpenReview 页面和 API 遇到浏览器验证，部分会议 virtual 页面被抓取限制。没有绕过验证，也没有仅凭第三方“oral 清单”把候选升级成已确认。
- NeurIPS 2026 主会在 12 月；其 [官方日期页](https://neurips.cc/Conferences/2026/Dates) 已列 9 月通知日期。会议尚未召开不代表尚未录用。本轮没有完成适合本产品的 NeurIPS 2026 oral / spotlight 一手交叉核验，因此不填充不确定名单。
- 以下工程模块和优先级属于本项目的设计推断；论文报告的性能不是本项目已实现或可保证的收益。

## Coding 相关补充：先采用机制，再验证收益

| 论文 | 会议与展示等级证据 | 对当前产品的价值 | 首版边界 |
| --- | --- | --- | --- |
| **Huxley-Gödel Machine: Human-Level Coding Agent Development by an Approximation of the Optimal Self-Improving Machine** | [ICLR 2026 官方 Downloads](https://iclr.cc/Downloads/2026) 可检索题目；[作者 Li Nanbo 主页](https://nanboli.github.io/) 标注 Oral；[原论文](https://arxiv.org/abs/2510.21614) | 搜索 coding harness 的改进版本；当前候选的单次 benchmark 分数不一定代表其后续改进潜力 | 用作后期离线 harness 搜索参考；不让首版运行时自动改写自身，不承诺跨模型或跨仓库的通用提升 |
| **daVinci-Dev: Agent-native Mid-training for Software Engineering** | [ICML 2026 proceedings](https://proceedings.mlr.press/v306/zeng26d.html) 确认主会收录；[作者代码仓](https://github.com/GAIR-NLP/daVinci-Dev) 标注 Oral | 轨迹保留完整信息流，并使用真实工具调用和测试产生的观察；适合指导 trace 数据契约 | 它是模型 mid-training 方法，不能作为普通 API 客户端的免费能力提升；首版只借鉴高质量轨迹采集 |
| **Improving Code Localization with Repository Memory** | [ICLR 2026 proceedings](https://proceedings.iclr.cc/paper_files/paper/2026/hash/b4c06f095368497f3ac19422efef8133-Abstract-Conference.html) 确认主会收录；未在本轮核实 Oral/Spotlight，故不贴该标签 | 从历史 commits、关联 issues、模块功能摘要检索，帮助定位修改位置 | 比通用无限记忆更贴近首版；必须绑定仓库与 revision，历史只能使用任务发生时已存在的内容，防止把目标修复泄漏给 agent |

HGM 的 [作者实现](https://github.com/metauto-ai/HGM) 可以作为研究参照；不建议先移植整个自改进搜索器。仓库记忆则可以从低复杂度基线开始：`rg`/符号检索、近期相关 commits、带源路径的模块摘要，统一纳入 context budget，再测是否减少无关读取并提高定位与修复成功率。结构化索引是否胜过简单搜索是实验问题，不先假定必须有图数据库或向量数据库。

**代码验证要与具体修订绑定。** 建议结果记录 `BaseRevision`、修改文件哈希、构建/测试命令、退出码、测试范围和输出引用；测试后文件发生变化，先前结果不能自动代表当前 diff。长会话 compaction 应保留目标、约束、已验证事实、失败尝试、未完成项与证据索引，不把完整工具输出无界塞回上下文。这些是本项目工程建议，不是上述论文已经证明的 C# 实现。

## 八篇通用 Agent 参考：展示等级的证据分开标注

以下论文的会议收录可在 [ICLR 官方 Downloads 2026](https://iclr.cc/Downloads/2026) 或 [ICML 官方 Downloads 2026](https://icml.cc/Downloads/2026) 中检索。**除生产研究的会议录播外，Oral 展示等级主要来自作者标注，本次未读到会议数据库的展示等级字段；不能表述为“会议官方 Oral 类型全部核实”。** Downloads 的 poster 链接本身不排除论文同时有 Oral。

| 论文与会议 | 一手类型证据 | 借鉴机制 | C# 产品落点 | 需要训练吗 / 主要边界 |
| --- | --- | --- | --- | --- |
| **Gaia2: Benchmarking LLM Agents on Dynamic and Asynchronous Environments — ICLR 2026，作者标注 Oral** | [作者 arXiv 注记](https://arxiv.org/abs/2602.11964) 明写 Oral | 环境独立演化；时间约束、异步事件、噪声、歧义；逐个写操作验证 | 事件驱动运行时；观察版本；等待/唤醒；写入前重新读状态；动作结果证据 | 评测不需训练；论文环境是模拟，不能直接外推真实桌面 |
| **OpenApps: Simulating Environment Variations to Measure UI-Agent Reliability — ICLR 2026，作者标注 Oral** | [作者 CV 第 1 页](https://arjunsubramonian.github.io/pdf/Arjun_Subramonian_CV.pdf) 明列 Oral | 同一任务改变界面外观、内容、布局，测稳定性而非一次演示 | UI 适配回归；主题、窗口大小、缩放、语言、弹窗、列表顺序变体；结果用应用状态核对 | 评测不需训练；模拟网页应用不等于原生桌面；代码/资产不是宽松商用许可证 |
| **GEPA: Reflective Prompt Evolution Can Outperform Reinforcement Learning — ICLR 2026，作者标注 Oral** | [作者 arXiv 注记](https://arxiv.org/abs/2507.19457) 明列 Oral | 从执行轨迹和自然语言反馈诊断失败；演化提示词并保留不同任务上互补的候选 | 离线 `SkillOptimizer`；skills 版本、评测记录、回滚；选择通过留出任务的版本 | 不更新基础模型权重，但需要优化调用预算、可靠反馈和评测隔离 |
| **Speculative Actions: A Lossless Framework for Faster Agentic Systems — ICLR 2026，作者标注 Oral** | [作者主页](https://liargkovas.com/) 2026 年 1 月消息明列 Oral | 快速预测未来动作，在慢调用返回前准备后续工作；确认匹配再提交 | 只读检索预取、候选解析与缓存；受预算约束的并发分支 | 不需训练；“lossless”依赖环境与提交语义，OS 部分明确是 lossy extension |
| **MemAgent: Reshaping Long-Context LLM with Multi-Conv RL-based Memory Agent — ICLR 2026，作者标注 Oral** | [作者 arXiv 注记](https://arxiv.org/abs/2507.02259) 明列 Oral | 分块读长文，在固定工作记忆中更新任务相关信息；独立上下文多轮训练 | 有界工作状态 + 原始证据仓；可重载文档分块；记忆更新接口 | 主要能力来自专门 RL 训练；普通 API summarizer 不能继承其长文本外推结果 |
| **In-the-Flow Agentic System Optimization for Effective Planning and Tool Use / AgentFlow — ICLR 2026，作者标注 Oral** | [Stanford 项目页](https://agentflow.stanford.edu/) 明列 Oral | planner / executor / verifier / generator 通过演化记忆协作；在实际多轮循环中训练 planner | 规划、执行、结果核对职责独立；明确的失败原因和重规划信号 | 完整方法需要 Flow-GRPO 训练；工程拆分可借鉴，性能提升不可直接归因于拆分 |
| **τ²-Bench: Evaluating Conversational Agents in a Dual-Control Environment — ICML 2026，作者标注 Oral** | [作者 Oral 讲稿](https://victorbarres.github.io/assets/html/tau2-bench-icml2026-oral.html) 明列会议和类型 | 用户和 agent 都能改环境；协作沟通必须与真实状态一致 | 用户接管/交还控制权；外部状态变更使旧计划失效；澄清之后继续任务 | 评测不需训练；客服/通信模拟结果不能充当桌面工作流完成率 |
| **Characterizing Agents in Production / Measuring Agents in Production — ICML 2026 Oral** | [会议录播](https://slideslive.com/39083750/measuring-agents-in-production) 列 ICML 2026 / Oral 6B；[IBM 论文页](https://research.ibm.com/publications/characterizing-agents-in-production) 确认正式题目与会议 | 生产系统经常采用短的可控流程、现成模型和系统层可靠性措施 | 先打通有限工作流、恢复与人工介入，避免用无界自治包装脆弱能力 | 观察性调研，不是架构的因果比较；不同稿件的样本口径有变化 |

### 1. Gaia2：迁移到 session 异步事件与代码工具状态

论文的关键不是又一个任务分数，而是环境不会等待 agent。用户更改目标、工具超时、新通知到达时，旧观察可能已经失效；动作执行顺序和时机本身构成正确性。[论文](https://arxiv.org/abs/2602.11964)、[作者 ARE 实现](https://github.com/facebookresearch/meta-agents-research-environments)。

建议运行时保存 `ObservationId`、`ObservedAt`、资源版本、任务约束、待完成操作和取消状态。工具调用成功只说明接口返回成功；`ActionVerifier` 应另外核对结果。首版的具体例子是：用户在测试期间修改文件、后台构建完成、终端进程超时、分支切换、编辑基于过时内容。应用补丁前检查目标版本，测试结果只绑定实际受测修订。上述字段和接口是工程设计，不是论文规定的 C# 实现。

对 GUI 的对应需求：时间线能显示“等待构建/测试”“文件已变化”“重新核对中”“需要用户输入”，恢复时保留此前证据与约束。ARE 为 MIT，可在后续扩展 work 场景时作为隔离的研究评测服务；首版不用把其模拟应用系统搬进产品。

### 2. OpenApps：全桌面能力后置，保留变体测试方法

OpenApps 原论文研究了六类可配置应用，说明同一 agent 在不同应用变体上可能有巨大波动。对本产品最有用的结论是，固定页面上的成功演示不足以证明适配能力。[论文](https://arxiv.org/abs/2511.20766)。

如果以后确实扩展通用桌面操作，再建立跨浏览器、文件管理器与文档应用的变体任务集。首版只验证自己的跨平台 GUI，以及用户明确纳入的少数 work 工作流，不同时承诺 UIA、macOS Accessibility、AT-SPI 全栈自动化。自己应用的主题、DPI、键盘输入与辅助功能测试属于 GUI 工程质量，并不等于 agent 会操作任意应用。

**复用限制必须跟随资产，而不能跟随“open-source”宣传。** 当前 [OpenApps 官方文档](https://facebookresearch.github.io/OpenApps/) 标明 CC-BY-NC；不能默认允许把其代码/数据并入商业产品或商业评测服务。其最新 theme/layout 轴也不与原论文像素完全一致，复现原论文应固定 `v1.0-paper`。本项目可借鉴变体测试方法并独立构建自有任务与应用。

### 3. GEPA：把技能改进放在可比较的离线流程

GEPA 对自然语言失败反馈进行反思，再搜索提示词候选并保留互补方案。作者报告的是特定任务和模型上的比较；不能将“更少 rollouts”宣传为任意桌面任务上的固定倍数优势。[正式 proceedings](https://proceedings.iclr.cc/paper_files/paper/2026/hash/0e9e708b6f48e14fd0ac29e167413f76-Abstract-Conference.html)、[作者代码（MIT）](https://github.com/gepa-ai/gepa)。

建议把一个 coding skill 表示为版本化文本、工具契约、适用范围和验收函数，例如定位编译错误、实施局部重构、补充回归测试。失败轨迹驱动候选更新；使用冻结开发任务筛选，跨 issue / 仓库留出任务验收，最后才进入默认技能库。不能让同一个 LLM 既修改 skill，又只凭“看起来更好”宣布成功。运行中的用户任务不应突然换到未经评测的新版本。

首次可保留 Python GEPA 研究工具，通过 JSONL 轨迹和结果与 .NET runtime 对接。若它被证明是核心运行时依赖，再考虑迁移；没有必要在方向尚未验证时重写优化器。

### 4. Speculative Actions：速度优化必须保留提交边界

最新版原稿明确区分 lossless 核心与 OS 上的 lossy 扩展；不同早期稿与后期稿中的速度数字也不同，本报告不将数字混用。[作者原稿](https://www.cs.columbia.edu/~kkaffes/papers/speculative-actions-iclr26.pdf)、[作者代码](https://github.com/naimengye/speculative-action)。

适合首轮实验的操作是相互独立的文件读取、符号/文本检索、Git 元数据读取和文档检索；结果附上输入与仓库版本，只有正式计划吻合时才消费。先做显式依赖分析后的只读并发，再决定是否值得预测下一步。写文件、Git 操作、安装依赖和运行会修改环境的测试不能当作普通只读预取；潜在写操作保留顺序与隔离边界。

`SpeculationScheduler` 的启用依据应是实测端到端延迟收益、取消成本、缓存命中和额外 API 费用；CPU 运行时更快不等于网络与模型等待自动缩短。

### 5. MemAgent 与 AgentFlow：学接口，训练收益另算

MemAgent 处理超长输入依赖经过训练的分块读写策略。C# 首版可实现有限工作记忆、原始记录索引和重载能力，但应把这称为自己的记忆基线；不能拿论文长文 QA 指标证明它有效。[作者项目](https://github.com/BytedTsinghua-SIA/MemAgent)。

AgentFlow 的规划、执行、核对分离可用于改善调试与失败恢复。完整系统的收益包含 Flow-GRPO 训练，因此“照着画四个角色”不是复现。[原论文](https://arxiv.org/abs/2510.05592)、[项目](https://agentflow.stanford.edu/)。建议优先使用确定性后置条件，只有需要语义判断时再调用 verifier 模型；无需每步强制四次 LLM 调用。

### 6. τ²-Bench 与生产研究：人机协作也是核心能力

用户会手动改代码、切换分支，也可能只愿意自己执行某一步。τ²-Bench 将用户也建模为能执行动作的参与者；可借鉴的是共同编辑时状态失效与接管机制，不是为首版增加客服模拟系统。[作者实现](https://github.com/sierra-research/tau2-bench)。

生产研究支持把可靠性与可控流程放在首位，但它是观察性研究，不证明“最多十步”应成为本产品的统一上限。正式页面使用 Characterizing，arXiv 使用 Measuring，样本口径也有修订；引用具体比例时应固定版本。[IBM 正式论文页](https://research.ibm.com/publications/characterizing-agents-in-production)、[arXiv v4](https://arxiv.org/abs/2512.04123)。

## 有价值但不能混进 Oral 主名单的补充

| 工作 | 已核实状态 | 值得借鉴 | 采用边界 |
| --- | --- | --- | --- |
| **Agentic Context Engineering（ACE）** | [作者宣布 ICLR 2026 接收](https://ace-agent.github.io/blogs/2026-01-30-iclr-acceptance/)；本表不将它标作主会 Oral | 增量更新结构化 playbook，保留细节，避免反复整体重写造成信息丢失 | 基于执行反馈更新仍可能写入错误经验；版本、来源、适用条件和撤销需要另行设计 |
| **Just-In-Time Reinforcement Learning（JitRL）** | [ICML 2026 proceedings](https://proceedings.mlr.press/v306/li26cv.html) 确认主会收录；Spotlight 展示等级本轮未独立复核，不以仅返回 Loading 的作者搜索页作为证据 | 检索经验估计动作优势，用于 test-time policy 调整 | “training-free”不等于 API 无要求：方法要调制输出 logits，普通闭源 tool-calling API 未必暴露所需能力 |
| **Reliable Weak-to-Strong Monitoring of LLM Agents** | [原论文](https://arxiv.org/abs/2508.19461) 与 ICLR 2026 下载目录可核对；本轮未完成一手 Oral 类型复核，不计入上述八篇 | 分层/时序轨迹监控，以及对监控器自身做对抗测试 | LLM monitor 会失误，不能替代确定性工具授权、资源隔离和结果检查 |

ACE 的具体机制以 [作者论文](https://arxiv.org/html/2510.04618v2) 为依据。建议记忆分开保存：用户明确事实、环境事实、任务工作状态、可复用操作经验。每条记录有来源、有效时间、适用对象、版本与可撤销性。这里的产品数据结构是工程推断，不能称为论文原样实现。

## 首批三个实验

1. **单 agent 代码验证与上下文基线。** 固定模型、仓库初态和预算，先比较普通工具循环与“编辑后构建/测试 + revision 绑定的验证”。在相同循环上再比较无持久记忆、普通摘要、带来源/版本与预算的仓库记忆，避免一次改多项无法归因。任务包含中途改文件、测试超时、失败修复、长会话恢复和旧上下文失效；验收代码行为、回归与当前 diff，而非 agent 自报完成。
2. **GEPA 离线 coding skill 优化。** 使用真实失败轨迹生成技能候选，对照固定技能和相同预算的人工基线；开发任务与跨 issue / 仓库留出任务隔离。代码可运行、测试有效和任务完成才产生主要反馈，避免技能记住具体补丁。版本化技能和证据，失败可回滚；这一步不训练基础模型权重。
3. **受限只读并行。** 先比较顺序执行与显式独立并发；只有其可靠且成本可控，再加入预算受限的推测预取。文件内容与 Git 状态附版本，写入顺序不变。先要求输出一致与无状态污染，再测 p50/p95、每成功任务成本及取消开销；没有实测收益的工具不启用推测。

实验实施、样本量、统计检验和发布门槛由项目主路线统一规定。以上均为待做实验，不构成任何性能声明。
