[English](README.md) | **简体中文**

# Agent 运行内核

Cogitide 的所有模型共用本模块。技术程序集和命名空间继续使用 `Workbench`，不会因为产品更名迁移用户数据。

| 文件 | 职责 |
| --- | --- |
| [AgentEngine.cs](AgentEngine.cs) | 模型/工具循环、工具审批、追加指令、上下文组装、规则重载、事件持久化、取消、终态与资源清理；同时包含当前有界上下文投影。 |

模型适配器只提供推理和原生状态，不拥有另一套工具内核。工具结果先保存，界面断开不能导致旧动作自动重跑。运行结束会等待所属后台资源清理；清理无法确认时保留失败/未知状态。工作区级排他由 [Host](../../Workbench.Host/README.md) 负责。

## 研究机制与本项目增量

- [Memory](../Memory/README.md) 实现 ACE 的增量经验条目和 APC 的有条件步骤复用切片；本模块将它们作为有来源的参考接入，经验不能替代用户指令或扩大工具权限。
- 可选 [ACON 启发的压缩指南](../Context/README.md) 在摘要与观察投影中使用；[RepoMem/DCI 仓库检索](../../Workbench.Tools/RepositoryMemory/README.md) 经运行作用域工具注入；[只读预取](../Research/Speculation/ReadOnlyPrefetch.cs) 在版本校验和预算下使用。关闭研究选项时保留成熟基线。
- [GEPA/SkillOpt 离线优化](../Research/SkillOptimization/README.md) 是显式启动的独立流程，不会由正常聊天自动启动，也不自动替换当前任务的技能。
- 本项目增加的是持久事件、来源/版本核对、steering 边界、未知动作不重放及资源所有权约束。这些属于工程设计增量，不声称完整复现论文或已证明学术新颖性、任务胜率提升。

摘要切点和技能发现的成熟来源分别记录在 [Compaction provenance](../Licenses/Compaction/PROVENANCE.md) 与 [Project reuse](../PROJECT-REUSE.md)。本模块没有整套复制 Pi/OpenCode 的运行时、事件总线或包管理器。

## 复用与贡献规则

并发和取消使用 .NET `SemaphoreSlim`、`ConcurrentDictionary`、`CancellationToken`；异步模型流使用 `IAsyncEnumerable`，工具目录使用 `FrozenDictionary`，运行结果使用 C# closed hierarchy，持久化使用现有 SQLite store。

**严禁重复造不必要的轮子。** 新入口必须复用此 AgentEngine，不得另建一套模型循环、审批或记忆服务；模型网络、JSON、数据库及进程实现应调用已有模块或成熟 .NET 组件。确有缺口时，在 PR 中说明不能复用的具体约束、已比较的方案和验证证据，不增加无依据的用户审批步骤。研究改动必须可关闭，并提供与基线相同条件下的独立验证。
