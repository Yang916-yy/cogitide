# 证据与项目经验

本目录保存能够追溯来源的观察和可复用步骤。它不训练模型，不把模型自述升级为事实，也不自动执行召回的命令。

| 文件 | 职责 |
| --- | --- |
| [EvidenceMemory.cs](EvidenceMemory.cs) | 从真实工具事件投影观察、产物、来源及保守失效状态，包含多文件补丁和后台命令终态。 |
| [SessionEvidenceTool.cs](SessionEvidenceTool.cs) | 本会话 `inspect_evidence` 工具，按来源回读有界证据。 |
| [ProjectExperienceTools.cs](ProjectExperienceTools.cs) | `remember_experience` / `recall_project_memory`，按项目保存、去重、显式替代及检索带前置条件的步骤候选。 |
| [EvidenceFileHash.cs](EvidenceFileHash.cs) | 共用有大小上限的流式文件哈希，避免各个记忆模块重复实现读取/校验。 |

## 论文与工程增量

采用 [ACE](https://arxiv.org/html/2510.04618v2) 的小条目增量更新思路，以及 [APC](https://proceedings.neurips.cc/paper_files/paper/2025/hash/9549f7d06700f0966d5f938f1d11022a-Abstract-Conference.html) 的有条件步骤复用思路。当前实现范围是可检索的 procedure 候选、适用条件与增量替代，不是完整 ACE Generator/Reflector/Curator 或 APC 论文系统。

Cogitide 增加了确定性来源和版本约束：只接受实际工具事件作为证据；多文件修改同时记录所有目标及删除来源；召回时重查源事件、结果和报告过的文件哈希；旧文件重新出现也会使删除观察失效。模型解释始终是 `Candidate`，文件版本一致只说明这些文件，没有证明整个依赖环境或自然语言经验正确。

项目经验、Git 历史定位和技能离线优化分别由本目录、[RepositoryMemory](../../Workbench.Tools/RepositoryMemory/README.md) 与 [SkillOptimization](../Research/SkillOptimization/README.md) 承担，不能混称已实现同一种论文算法。来源关系与范围详见 [PROJECT-REUSE](../PROJECT-REUSE.md)。工程增量尚无独立基准证明收益或学术新颖性。

## 组件与贡献约束

复用现有 [SQLite 会话事件库](../Persistence/README.md)、.NET SHA-256、异步流、JSON 源生成、不可变 record 和有界关键词匹配；没有新增向量库、图数据库或记忆服务。保存前校验完整召回包预算，避免“能保存但永远取不出”的条目。

**严禁重复造不必要的轮子。** 不得另建并行的事件存储、哈希实现或自动脚本执行器；优先复用本模块和成熟 .NET 组件。新增索引/检索器必须先说明现有路径与关键词检索的具体不足、替代方案、迁移和对照结果。贡献不得删除原始失败记录、隐式提高经验权威或用同一模型自评代替冻结任务验证。
