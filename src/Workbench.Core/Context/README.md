# Context：摘要、观察投影与压缩指南

这是内核模块；稳定的 Pi/OpenCode 压缩基线与可关闭的 ACON 启发机制放在一起，复用同一消息、模型接口、取消与存储协议。源码文件 3 个；文件统计由根目录模块清单统一生成。

| 文件 | 职责与来源 |
| --- | --- |
| `ConversationCompaction.cs` | Pi 的完整工具轮次切点、结构化历史摘要及更新；OpenCode 的溢出前预留与规则重载。固定上游、改编范围和原始 MIT 许可见 [既有 provenance](../Licenses/Compaction/PROVENANCE.md)。新版本增加可选压缩指南及独立保留的来源锚点。 |
| `CompressionGuidance.cs` | 独立 C# 实现的版本化指南、观察投影和来源锚点选择。只改变发送给模型的视图，原始消息、工具结果、调用配对及事件不变。 |
| `CompressionGuidanceOptimizer.cs` | 显式执行的一次失败诊断提案，以及开发/选择集隔离的成对验收。返回候选或被选择的指南，不自动启用或后台自我改写。 |

## 采用了什么论文机制

来源为 [ACON v3，§3.2–3.3 与算法 1](https://arxiv.org/html/2510.00615v3)：分别处理历史和观察的上下文成本，通过原始/压缩轨迹的失败差异修改自然语言压缩指南，再比较任务效果与上下文大小。作者代码位于 [microsoft/acon](https://github.com/microsoft/acon)。本模块依论文概念独立实现，**没有复制作者 Python 代码、提示词或数据**；没有把蒸馏、模型权重更新或完整基准搬入产品。

运行时包含两个互补动作：

1. `ProjectObservations` 对过长工具结果创建有显式省略标记的 JSON 视图，保留可容纳的路径、哈希、状态、退出码和分页游标，以及有限头尾片段。身份仍绑定原 `ToolCallId`；完整结果保留在事件/会话库。元数据无法安全装入预算时保留原观察，避免静默漏字段。这是确定性观察压缩，并非论文的第二个 LLM compressor。
2. `Prepare/GenerateAsync` 用冻结版本的指南调用现有模型生成历史摘要。来源路径、错误、待办提示和工具结果引用由宿主提取为有预算的附录；模型不能删除这些附录。原始用户指令另行保留，模型私有 reasoning 不送入摘要。失败、取消、格式或预算异常不会提交新 checkpoint。

指南由 `SchemaVersion`、`Id`、`Revision` 和 SHA-256 标识；checkpoint 的来源附录记录所用版本及内容哈希。默认关闭研究选项时沿用原有摘要路径。启用 `RunOptions.Research.CompressionGuidance` 使用默认指南，`CompressionGuidancePath` 可指定工作区内已选择的 JSON 文件，开始本轮任务时只加载一次。

## 本项目增加了什么

- **原始记录与压缩视图分离**：丢失摘要不会丢失原始工具结果；重新检索不重放动作。
- **独立来源附录**：来源值按原消息位置记录、按实际编码字符数预算；裁剪避开 Unicode 代理项中间。锚点是有界历史片段，未声称覆盖所有语义或证明当前文件正确。
- **先守住成功，再减少上下文**：指南候选不得让任何原本成功的选择题失败，也不得丢失评测器要求的信号；在此基础上才接受任务成功数提高，或成功数相同时上下文更小的方案。
- **可核对的选择边界**：失败诊断只能来自 development，成对选择只能来自不同 ID 的 selection，final-test 不参与选择。调用次数、请求/输出字符、时间与已报告 token 均设上限；未知模型用量不当作零成本接受。

这些属于针对编码 agent 的工程改造与设计取舍，**没有证明论文级的新颖性，也未测得任务成功率、费用或延迟优势**。字符数是上下文代理指标，不是精确 token 或 API 价格；压缩可能损害缓存收益。回调评测器必须执行冻结验收，不能只让生成候选的模型自评分。

## 实际调用与导出

```csharp
var baseline = await CompressionGuidance.LoadAsync("guideline.json", ct);
var proposal = await CompressionGuidanceOptimizer.ProposeAsync(
    baseline, developmentFailures, existingAgentModel, budget, ct);
var result = await CompressionGuidanceOptimizer.CompareAsync(
    baseline, proposal, selectionCases, EvaluateFrozenCaseAsync, budget, ct);
if (result.Accepted)
    await result.Selected.SaveAsync("selected-guideline.json", ct);
```

`EvaluateFrozenCaseAsync(guidance, task, remainingTokens, ct)` 对同一选择题分别跑 baseline 和 candidate，返回任务是否通过、要求的信号是否保留、上下文字符数和真实已报告 input/output token。`remainingTokens` 是扣除提案及先前评测后的输入与输出合计余额；评测器须在每次实际模型调用前检查该余额，不能把它分别当成两份额度。旧三参数回调仅保留接口兼容。`ValidateConfiguration` 可在无密钥、无模型和无产物写入时预检配置。

只有显式调用以上 API 才发生提案与评测；普通 agent 运行不会额外启动反思、训练或收费评测。模型与环境的完整评估协议需由调用方固定，最终留出测试另跑；不把一次选择成功当成独立研究复现。宿主 `--optimize-compression SPEC.json --output NEW_DIRECTORY --validate-only|--execute` 提供实际入口，先固定选择，再报告最终留出结果；产物不会自动启用。后台端到端夹具见 [verify-research.py](../../../scripts/verify-research.py)。

验证代码位于 [ResearchCompressionTests.cs](../../../tests/Workbench.Tests/ResearchCompressionTests.cs)：关闭时的基线、真实模型接口传入指南、原始信息/配对保留、Unicode、失败不提交、版本文件、提案工具拒绝、开发/选择/测试隔离、退化拒绝与预算停止。测试是本地确定性夹具，验收数量以根目录最新报告为准。

## 贡献约束：禁止不必要的重复造轮子

**严禁后续贡献者在这里另写 HTTP/SSE 客户端、JSON/YAML 解析器、正则引擎、数据库、线程池、通用异步调度器或模型 SDK。** 使用现有 `IAgentModel`、`System.Text.Json` 源生成、BCL `Regex`/`GeneratedRegex`、SHA-256、异步流与 `CancellationToken`，持久化沿用已有 SQLite/事件接口。不要为 ACON 再建一套 agent runtime。

新增依赖或基础设施替代必须在变更说明里给出已有组件不能满足的具体行为和验证证据，并补来源、版本、许可证及边界说明。改变压缩/选择策略必须同时提交固定测试和失败案例，保留关闭开关；不得删原始记录、放宽权限、使用最终测试集挑选指南，或引用原论文百分比作为本产品收益。
