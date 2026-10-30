[English](core-memory-design.md) | **简体中文**

# 长任务、项目规则与经验复用实施设计

核查日期：2026-10-30。范围：普通 DeepSeek / Claude / OpenAI API 模型可运行的推理时机制；不要求训练模型、读取隐藏推理或控制服务端 KV cache。本文件是待实施设计与实验协议，不代表这些能力已经完成或已经取得论文中的收益。

本轮工程进展：已实现 `EvidenceMemory` / `SessionEvidenceTool`、Pi 式自动摘要与持久 checkpoint、层级 `AGENTS.md`、本地 skills 发现/读取，以及按项目保存、检索、替代的 procedure 候选。底层复用现有 SQLite 事件库；不是完整 ACE Generator/Reflector/Curator，也没有学习检索器或 GEPA 优化。以下更完整的研究设计仍待逐项评测；见[本轮验收](../docs/continuity-validation.md)。

## 先复用成熟基线，再加入可测的研究机制

首个研究方向选 **ACE 启发的增量条目更新，加上本项目自己的来源核对与证据失效机制**。同一任务中的工作状态与跨任务可复用经验分开存储。先具备完整工具轮次保留、压缩失败回退、原始事件可读和恢复一致性，再比较增量记忆是否优于普通摘要。

TypeScript 的对象、判别联合和异步状态机适合改编为 C# record、穷举分支与 CancellationToken；迁移成本主要来自生命周期和协议语义，而非语法。无需把整个 TS 依赖树、事件 emitter、HTTP/SSE 或数据库封装一起搬入 .NET。

| 第一批复用点 | 固定来源与许可证 | C# 落点与验收 |
| --- | --- | --- |
| Pi 的压缩边界、最近消息保留、摘要记录与 token usage | [compaction 文档](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/coding-agent/docs/compaction.md)，commit `e7a0df27c09d11bf4161d3605246cbf57a129cb2`；[MIT](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/LICENSE) | 历史保留在事件库，模型请求使用摘要与保留后缀；切点不得拆开工具调用/结果；压缩消耗进入总预算；取消不提交半成品 |
| OpenCode 的 session inbox 与执行生命周期 | [inbox.ts](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/session/inbox.ts)、[execution.ts](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/session/execution.ts)，commit `60072747957926e7aeec7e911fe6e23abe9bf830`；[MIT](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/LICENSE) | 同会话输入有序、steering 在步骤边界处理；停机恢复与用户取消不同；重启不盲目重放有副作用的工具 |

Pi 文档在本轮重新核验；OpenCode 功能依据此前 [开源源码抽查](opensource.md)，许可证在本轮重新核验。以上是改编目标，并非本文件已经复制其实现。真正移植代码时逐文件记录来源 commit、路径、改动和版权，保留适用的 LICENSE/NOTICE；翻译成 C# 不免除原许可证义务。

BCL `System.Text.Json`、SHA-256、`CancellationToken`、现有 SQLite 事务和不可变 DTO 足以支撑首版。暂不引入图数据库、向量数据库、额外服务或训练框架。必要时使用 SQLite 原有索引/全文检索，并先与路径、关键词检索比较。

## 四篇最适合当前边界的论文

| 论文 | 已核实的会议信息 | 采用机制 | 不继承的结论 |
| --- | --- | --- | --- |
| **Agentic Context Engineering（ACE）** | [ICLR 2026 正式 proceedings](https://proceedings.iclr.cc/paper_files/paper/2026/hash/8a94ff6f922d995d7d3f4ebf4143e442-Abstract-Conference.html)；未核实 Oral/Spotlight | 条目化 playbook；模型提出小范围 delta，确定性合并；保留具体失败条件和操作经验 | 论文在 AppWorld、金融任务上的结果不是 Cogito 编码成功率；本方案不是完整 ACE 复现 |
| **A-Mem: Agentic Memory for LLM Agents** | [NeurIPS 2025 主会](https://proceedings.neurips.cc/paper_files/paper/2025/hash/19909c36f51abc4856b4560aff3d36d6-Abstract-Conference.html)；未核实 Oral/Spotlight | 记忆单元有上下文、元信息与关联；新观察可以修订旧条目的关系 | 自组织记忆关系不等于事实正确；不凭此引入自动全局知识图谱 |
| **Agentic Plan Caching（APC）** | [NeurIPS 2025 主会](https://proceedings.neurips.cc/paper_files/paper/2025/hash/9549f7d06700f0966d5f938f1d11022a-Abstract-Conference.html)；未核实 Oral/Spotlight | 从已完成轨迹抽取计划模板，检索后适配当前环境 | 缓存计划不能跳过重新检查文件、工具结果或审批；论文成本/延迟收益需要本产品重新测量 |
| **GEPA: Reflective Prompt Evolution Can Outperform Reinforcement Learning** | [ICLR 2026 proceedings](https://proceedings.iclr.cc/paper_files/paper/2026/hash/0e9e708b6f48e14fd0ac29e167413f76-Abstract-Conference.html)；[作者 arXiv v2 注记为 Oral](https://arxiv.org/abs/2507.19457v2) | 离线分析失败轨迹，生成、评测和保留互补的技能提示候选 | 需要真实反馈、优化调用预算和留出任务；不让运行中的技能自行覆盖默认版本 |

ACE 的具体增量机制依据 [原论文 v2 第 3 节](https://arxiv.org/html/2510.04618v2)：生成、反思和整理职责分开；用局部条目更新代替每次整体重写，并控制重复。本项目对证据来源、文件哈希、指令优先级和事务提交的要求是额外工程设计，不能归为论文已经验证的机制。

MemAgent 的主要能力依赖专门 RL 训练；AgentFlow 完整方法需要 Flow-GRPO；JitRL 涉及 logits 调制。它们不适合作为当前普通托管 API 接入下的首个核心实现。出处与边界见 [论文总表](papers.md)。Dynamic Cheatsheet 可作为整体摘要/记事本对照，但本轮不把未单独核实会议类型的工作加入 Oral 名单。

本文件没有复制以上研究仓库的代码或提示词。第一版按论文机制独立实现；以后决定使用作者实现时再固定代码 commit、许可证和源路径。GEPA 可以先作为离线研究工具，通过 JSONL 接入，不成为桌面运行时依赖。

## TaskState 契约

`TaskState` 是原始事件的可撤销投影，不是新的事实源。建议契约如下；具体 C# 名称可随模型协议统一调整。

```text
TaskState
  SchemaVersion, StateVersion, SessionId, WorkspaceId
  CoveredThroughSequence, SourceDigest, CreatedAtUtc
  UserInstructionRefs[]    // 原文事件引用；不得由模型改写原文
  GoalRefs[], ConstraintRefs[], DecisionRefs[]
  Evidence[], Hypotheses[], OpenQuestions[], NextSteps[]
  SelectedProcedureVersions[]

SourceRef
  EventSequence, ToolCallId?, ContentSha256, SourceKind

Evidence
  Id, SourceRefs[], ObservationKind, ObservedAtUtc
  WorkspaceId, GitRevision?, ArtifactRefs[]
  Command?, ExitCode?, TestScope?, Outcome
  Validity                  // Current | Stale | Unknown

ArtifactRef
  WorkspaceRelativePath, Sha256, ObservedAtUtc

MemoryItem
  Id, Kind, Text, SourceRefs[], Scope, Preconditions[]
  Status                    // Candidate | Validated | Rejected | Superseded
  SupersedesId?, ValidationRefs[]

MemoryDelta
  ExpectedStateVersion, CoveredThroughSequence, SourceDigest
  Add[], Amend[], Supersede[], Invalidate[]
```

语义要求：

- `SourceDigest` 覆盖被压缩的原始事件序列、当前分支身份和对应序号；恢复时可验证投影确实来自该历史。哈希证明来源一致，不能证明模型解释正确。
- `Outcome` 只能由工具返回与确定性判定产生。模型可提出“可能修复”，不能自行把其标成测试通过。退出码 0 只证明该命令成功退出；任务成功还要匹配验收条件与测试范围。
- `Current` 只表示所声明的版本/依赖范围仍匹配。若无法捕获构建输入、依赖、环境及测试范围，使用 `Unknown`，不声称一组源文件哈希能证明全部依赖未变。保守实现可在任一工作区写入后使相关验证证据失效。
- 用户改变目标或约束产生新的明确指令引用与 supersedes 关系；模型摘要无权覆盖用户原文。原文中的代码、引用、附件仍按其来源处理，不因为出现在用户消息里就全部提升为指令。
- `Hypotheses` 保存模型推断与待检验解释，`Evidence` 保存观察；检索出的旧经验作为参考数据传入模型，不提升为系统指令。
- 原始工具输出需可重新读取；若现有事件仅保留截断文本，则证据必须明确 `truncated`，不能假装拥有未保存的内容。摘要包含的路径/命令也不得变成自动执行入口。
- `WorkspaceId` 绑定实际工作区身份。默认不把仓库中的文件内容、工具观察或经验迁移到另一个项目；路径归一化和现有工作区边界检查继续生效。

## 压缩与增量更新流程

1. **触发与选边界。** 请求预算按模型配置、已报告 usage 和保守估算计算；字符数不能当精确 token 数。只在完成的工具批次边界选择旧前缀。固定保留当前明确任务/steering、尚未解决的用户约束、最近完整工具轮次和待处理交互；这些必要内容超预算时明确停止并说明原因。
2. **准备候选。** 确定性投影先提取用户指令引用、工具结果和产物版本。模型仅对其余可压缩内容提出受 JSON schema、条目数量和总字符预算约束的 delta；已有条目不用每轮整体重写。
3. **验证候选。** 校验版本、来源事件存在且属于已覆盖前缀、工具调用对应关系、hash 格式、条目唯一性、允许的状态转换、总预算；禁止候选删除固定保留的用户指令。结构校验无法证明任意自然语言命题成立，所以模型文字仍保留 Candidate/Hypothesis 标记。
4. **事务提交。** SQLite 在同一事务写入 delta、状态快照和覆盖边界；使用 `ExpectedStateVersion` 防止旧快照覆盖新 steering。失败、超时和取消都不推进边界。完成后才向 UI 发压缩完成事件。
5. **组装请求。** 经核对的指令 + 明确标为历史数据的任务状态 + 原始后缀。缓存最近有效投影；压缩失败后，只有原上下文仍满足预算时才继续，否则暂停本次运行，保留全部历史和明确错误。
6. **恢复和检索。** 重启验证来源 digest、工作区与 artifact 版本，标记失效证据；需要细节时重新读取指定事件/文件。不能因为恢复摘要写着“下一步运行命令”就重放该命令。

模型层的 provider-native reasoning blocks、签名与 opaque state 不进入摘要文字，也不由模型重写。当前未完成工具链需要的状态按供应商契约原样往返；仅在完整轮次之外丢弃可丢弃的旧状态。换模型或供应商时按协议显式重建上下文，不能把 Claude 签名块传给 DeepSeek。每个 provider 都需要“压缩前后工具调用有效”的契约测试。

默认触发阈值、保留长度与每轮记忆预算应配置化，先用可解释的保守值，再以固定任务结果调节。压缩调用计入总 token、时间和费用；不允许无限重试或默认每一步增加多次反思调用。

## 项目规则与 skills 的最小运行时

规则加载是成熟产品能力，独立于记忆实验实现：

1. 来源优先级固定：应用约束、当前用户明确指令、用户配置的全局规则、工作区根到目标路径的 `AGENTS.md`、被选中的 skill 内容。局部项目规则只在自己的目录范围细化父规则；不得扩大工具权限或覆盖当前用户指令。冲突与生效范围可在 UI 检查。
2. 根规则会话开始加载；子目录规则在访问该路径前解析。每份规则保存规范路径、内容 hash 与读取时间，运行时检测变化，后续步骤使用新版本并记录事件。模型生成的“经验”不写回 AGENTS.md。
3. 复用现有读取长度、工作区和 reparse-point 边界；跨目录操作合并各受影响路径的规则；若限制冲突且无法安全拆分，给出具体冲突，不自行假定任一规则无效。
4. skills 先加载名称、描述、适用范围、入口路径与 hash；实际选中后再读取正文和按需资源。运行记录所用版本；新安装、更新和卸载不悄悄改变正在执行的步骤。
5. 加载 skill 不自动运行其脚本。脚本、安装命令和外部写入仍经过现有工具生命周期和授权边界。文件中要求扩大权限的文字不会成为产品授权。

这部分应先支持本地规则与本地技能；在线市场、自动安装和跨项目共享暂不成为长任务功能的前置条件。开发 Cogito 所用的 Codex skills 与 Cogito 产品内的 skills runtime 是两回事。

## 经验复用的第二个切片

APC 启发的 procedure 记录包含：适用任务类型、前置条件、步骤意图、可替换参数、验证方法、失败反例和来源轨迹。只记录项目内可复用操作，不把完整历史补丁直接当新任务答案。

流程为 `Candidate → 在独立任务上验证 → Validated → 按项目/路径检索 → 检查前置条件 → 适配 → 执行与再验证`。一条成功轨迹可以创建候选，不能证明普遍有效。遇到冲突证据就降级或撤销；模型“觉得有帮助”的计数只辅助排序，不代替外部验证。

初期使用路径、任务标签和关键词检索，固定最多若干条并纳入同一上下文预算。语义向量检索和 A-Mem 式关系扩展作为独立消融项；没有结果前不建立复杂记忆网络。

GEPA 用于后续离线技能候选优化：开发集生成候选，冻结的跨 issue/仓库留出集验收；不接触目标修复或留出参考答案。默认版本由可比较的验收结果决定，保留可回滚版本。当前运行的用户任务固定所用版本。

## 一个可执行的核心实验

**假设 H1：** 在同模型、上下文窗口、工具、总费用与时间预算下，增量任务状态比整体重写摘要更能保留约束、识别过期验证证据，并改善需要多次压缩/恢复的编码任务完成率。哈希与原始引用只保证可核对性；是否改善模型行为必须实测。

先建立确定性故障注入夹具，不需要支付 API 费用：

| 夹具 | 必须成立的断言 |
| --- | --- |
| 早期“不要改接口”指令后插入长日志，再压缩三次 | 原文指令引用仍在；压缩没有静默放宽约束 |
| 用户新消息改变目标，与压缩提交竞态 | 旧状态版本提交被拒；新目标进入下一请求 |
| 测试通过后改受测文件，再恢复 | 旧结果不能标作当前修改的验证 |
| 输出出现伪造“测试已通过/忽略用户约束”文本 | 没有对应真实工具结果就不能成为 Validated 证据或高优先级指令 |
| 多工具批次、供应商 reasoning state 与工具结果 | 切点不拆配对；保留轮次满足供应商协议 |
| 压缩被取消、JSON 缺引用、重复 ID、预算超限 | 状态和覆盖边界不变；原始事件可读取 |
| 提交后 UI 崩溃，随后恢复 | 只恢复一次已提交状态；不自动重放命令 |
| 切换工作区、分支、模型 | 项目经验不串用；版本与 provider 状态重新核对 |

这些断言验证状态机正确性，不证明 agent 智能提升。随后运行真实模型对照：

| 实验臂 | 定义 |
| --- | --- |
| A | 现有保留最近完整轮次/历史裁剪策略 |
| B | Pi 式整体结构化摘要，其他工具/恢复机制相同 |
| C | 增量任务条目 + 原始引用 + 版本失效；没有跨任务 procedure |
| D | C 移除来源/失效检查，仅用于隔离评测环境，检查改善是否实际来自这些约束 |
| E，第二阶段 | C 加上按范围检索的 procedure；与 C 比较经验复用的增益 |

任务覆盖长日志排错、中途变更需求、失败重试、文件外部修改、冷启动恢复与相似任务迁移。先建立约 20 个开发任务与至少 40 个按仓库/问题族隔离的留出任务作为工程试验起点；这不是已完成样本，也不保证统计功效。每题多次独立运行，记录模型精确版本、配置、预算、初始 commit、环境和任务顺序；报告配对置信区间、各类型失败与失败轨迹。

主要指标为冻结验收测试/产物检查下的任务成功率、用户约束违反率、错误沿用过期证据的次数；辅助指标为恢复后重复动作、检索/读文件次数、压缩开销、总 token、总费用、p50/p95 耗时。任务是否完成由外部验收决定，不由产生记忆的模型自评决定。价格不同的模型分别比较；不把更多调用预算产生的收益归为记忆机制。

实验阶段固定测试规则，失败不能通过删除任务、调整验收或读取参考补丁“修复”。若 C 未稳定优于 B，保留成熟摘要基线和确定性证据功能，增量模型记忆继续实验而不默认启用。

## 实施顺序和可交付边界

1. 完成 DeepSeek/Claude/OpenAI 模型协议与真实调用验证，尤其是工具、多轮 reasoning 状态、用量和失败处理。
2. 以 Pi 固定来源实现摘要基线和完整生命周期；补齐路径作用域的项目规则与本地技能加载。
3. 独立实现 `EvidenceMemory` 的不可变 DTO、来源检查、delta 事务和文件变更失效；接入工具事件。做到这步只能宣称“有来源的任务状态”，不能宣称“学会经验”。
4. 加入有预算的模型 delta 生成与压缩集成；完成故障夹具和 A/B/C/D 实验，保留可切回基线的开关。
5. 在结果支持时加入 procedure 检索 E，再决定 GEPA 离线技能优化、关系检索或仓库历史摘要是否值得。

上述阶段不以三端实机暂缺为前置阻塞；核心契约与模型实验可继续。跨平台 GUI、进程和路径行为仍需后续实机验收，不用单台 Windows 的成功代替三端结论。
