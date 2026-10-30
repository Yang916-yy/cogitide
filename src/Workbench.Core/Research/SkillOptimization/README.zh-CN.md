[English](README.md) | **简体中文**

# 离线技能优化：GEPA 与 SkillOpt 的可执行切片

本目录属于 agent 核心研究模块。它在**用户显式启动的离线运行**中优化一份技能文本；普通聊天不会自行启动优化、调用评测模型或安装候选技能。底层模型权重保持不变。工程验证与论文效果复现分别记录，不能把夹具通过写成编码成功率提升。

## 采用哪些论文机制

| 来源 | 本目录实现 | 有意保留的边界 |
| --- | --- | --- |
| [GEPA v2，§3 与算法 1/2](https://arxiv.org/html/2507.19457v2) | 从训练执行反馈反思、提出文本 mutation；训练分数改善才进入选择集评测；按选择集中每个任务的赢家构造集合、剔除支配项，以赢得任务的次数加权抽样父候选；可选每三轮合并两个互补父候选；最终按选择集均分冻结候选。 | 单个技能模块、固定小训练批次；没有完整 compound-AI 模块调度、作者 merge 实现或论文数据。合并是提示模型整合两份文本的有限机制，不声称等同作者的 system-aware crossover。 |
| [SkillOpt 论文](https://arxiv.org/abs/2605.23904)、[作者复现指南](https://microsoft.github.io/SkillOpt/docs/guideline.html) | 训练失败与约束结果形成结构化反馈；对当前技能作有大小上限的修改；只有选择集均分提高且每个任务均不退步，才更新 incumbent。 | 本项目采用更保守的逐任务 non-regression gate；没有 slow-update、meta-skill、Sleep 后台服务、全套 benchmark runner 或训练算法性能复现。 |

这是根据论文机制**独立实现的 C# 代码**，没有复制作者 Python 源码、元提示词或数据。参考日期为 2026-10-30；GEPA 使用固定 arXiv v2，SkillOpt 网页跟随作者 main，网页中的部署 preview 不能替代论文 runner。若以后直接移植作者代码，必须另行记录实际 commit、源文件、修改说明，保留适用 LICENSE/NOTICE；本项目的 Apache-2.0 声明不抹去第三方许可。

## 本项目增加的工程机制

这些是服务于桌面编码 agent 的设计增量，**不是已经证明具有学术新颖性的算法声明**：

- 同一执行、预算和产物协议支持两种不同的搜索策略，避免分别建立一套模型/工具运行时。
- 训练、选择、最终测试由显式分区和不同任务族隔离，并用 BCL `JsonElement.DeepEquals` 拒绝跨分区的相同任务载荷。近似重复仍需要数据集作者按任务族识别。反思模型只看到训练任务及其诊断，无法通过本接口读取选择集内容或最终测试内容。
- `selection_frozen` 记录写入后才运行 baseline 与选定技能的最终测试。测试结果只报告，不反过来选择另一份技能；即使最终测试变差，报告仍保留原选定 ID。
- 技能有内容 SHA-256、父版本和 generation；任务 manifest 有哈希。保留失败候选、拒绝原因、原始分数、约束结果、反馈和用量，不只留下赢家。
- SkillOpt 更新还检查受保护原文和改动跨度。它是确定性文本边界，不可能自动证明自然语言技能没有隐含错误。
- 导出到新的离线产物目录；不覆盖已运行目录、不恢复一半运行、不写入活动的 `SKILL.md`。`selected-skill.md` 只是供审阅的结果，`AutomaticallyActivated` 始终为 false。

## 文件职责与集成

| 文件 | 职责 |
| --- | --- |
| `SkillOptimizationContracts.cs` | 两种策略共享的任务、技能候选、评测、反馈、预算、报告及源生成 JSON 契约。 |
| `SkillOptimizer.cs` | 候选搜索、门控、分区隔离、预算统计、最终测试顺序及原子报告保存。 |
| `ModelSkillCandidateProposer.cs` | 复用现有 `IAgentModel` 做一次反思提案；无工具、无隐式重试；只接受有界的 `{skill, reflection}` JSON。 |

Host 提供 `ISkillCandidateEvaluator`：每个候选/任务使用独立生成的工作区，固定初始 Git revision、目标模型和验收命令，调用现有 AgentEngine，并把测试结果变成 `[0,1]` 分数。评分不能由被优化模型自行宣布。`Payload` 的格式由该评测适配器拥有，优化器不执行其中的命令。

```csharp
var report = await new SkillOptimizer().OptimizeAsync(
    initialSkill, partitionedTasks,
    new ModelSkillCandidateProposer(optimizerModel, explicitPricing),
    isolatedTaskEvaluator, options, newEmptyArtifactDirectory, cancellationToken);
```

评测适配器必须遵守传入的 `SkillOperationBudget`：每次模型调用之前检查剩余次数、时间和已观察用量，汇总普通推理、摘要等所有调用。当前内核独立控制 proposer/evaluator 调用次数和取消期限，要求回报真实累计 `SkillUsage`；缺失 token 用量时停止，设置了费用上限但缺少价格时也停止。

**token/费用按服务返回用量检查，不能撤销已经在途的调用。** 单个在途请求可能越过余额，报告将记录实际用量并标记 `budget_exceeded`，不继续请求。提供方输出上限、适配器逐次检查和可靠取消共同限制该窗口；不要把这种统计保护写成对任意外部 evaluator 的硬费用保证。未提供价格时保留费用未知，不能记成免费；异常或取消打断一个未回报的操作时，`AllModelCallsKnown`、`AllTokenUsageKnown` 和 `AllCostsKnown` 明确标记为 false，数值字段只表示已确认部分。

每次运行生成 `tasks.json`、`candidates/<内容ID>/SKILL.md`、候选元数据、`report.json` 和 `selected-skill.md`。报告采用同目录临时文件与替换提交；这不是多文件断电事务。目录拒绝现有内容和符号链接/连接点，候选文件名由内容哈希生成，模型无法选择写入路径。

## 组件复用与贡献约束

复用现有模型适配器及其官方 SDK、`System.Text.Json` 源生成、`SHA256`、`FileStream`、`CancellationToken`、`Stopwatch`、LINQ 和 `Random`。没有安装第二套模型客户端、任务调度服务、向量库、训练框架或数据库。

**严禁后续贡献者重复造不必要的轮子。** 网络/SSE/认证继续使用现有模型层；真实编码执行继续使用 AgentEngine；进程、存储和 JSON 优先使用 .NET/BCL 或已经采用的成熟组件。新增自实现必须说明现有组件无法满足的具体约束、替代方案和验证证据。研究策略不得以“自进化”为理由绕过工具审批、读取最终测试答案、修改冻结验收或自动扩大调用预算。

验证位于 `tests/Workbench.Tests/SkillOptimizationTests.cs`：互补 Pareto 候选与合并、SkillOpt 平均分提高但单任务退步的拒绝、受保护文本、训练/选择/测试隔离、预算与取消、完整产物、以及真实 OpenAI SDK 的本地 SSE 提案协议。真实仓库模型评测由 Host 集成夹具覆盖；本目录测试不冒充论文 benchmark 复现。
