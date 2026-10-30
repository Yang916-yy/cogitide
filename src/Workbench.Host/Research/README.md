# Research CLI：显式离线评测与导出

外围执行适配器，复用 `AgentEngine`、官方模型 SDK、`WorkspaceService` 的 argv 进程执行、Git 与 SQLite；不引入第二套 agent、网络栈或调度框架。算法属于 [SkillOptimization](../../Workbench.Core/Research/SkillOptimization/README.md) 与 [Context](../../Workbench.Core/Context/README.md)，本目录将它们接到真实编码任务与冻结测试上。

```text
Workbench.Host --optimize-skill SPEC.json --output NEW_DIRECTORY --validate-only
Workbench.Host --optimize-skill SPEC.json --output NEW_DIRECTORY --execute
Workbench.Host --optimize-compression SPEC.json --output NEW_DIRECTORY --execute
```

`--validate-only` 校验配置，不取密钥、不调用模型、不执行命令、不创建输出。`--execute` 才开始实际调用，可能收费；密钥只从 `provider.apiKeyEnvironmentVariable` 读取，配置中不写密钥。所有调用（含自动摘要）计入评测用量；设置预算不能撤销在途请求，服务未回报的用量保持未知。

Spec 的完整结构由 `ResearchOptimizationSpec` / `ResearchTaskPayload` 定义。可参考 [离线夹具](../../../scripts/verify-research.py) 生成的 manifest；它使用本地 SSE 模型，默认不会访问付费服务。配置包含 provider、initialSkill、策略与预算、带 family 的 train/selection/holdout tasks；每个 payload 指定绝对本地 repository、完整 baseCommit、prompt、validationCommands、protectedPaths 和可选 allowedCommands。压缩优化另含 development-only `compressionFailures` 和 `compressionBudget`。

每项评测在新的目录 `git init` 后只浅取指定 commit，不复制未来 refs/对象，不改输入仓库。使用相同内核执行，评分来自冻结验证命令。受保护文件的直接写入/patch 被拒绝；批准动作前和验证前检查文件哈希；验收被改动则跳过执行并记失败。自定义密钥环境变量也从子进程中移除。候选只导出，绝不覆盖活动技能或自动安装。选定版本冻结后才进行 holdout；测试结果不返回优化器选择步骤。

输出记录候选/报告/事件/验证输出/用量和终止状态，Ctrl+C 触发原取消链。未知或超预算结果不当作零成本成功。验证脚本、仓库和显式允许的命令必须可信：这是独立工作区与应用权限控制，**不是操作系统沙箱**，不能防御任意恶意本地程序修改再恢复文件、读宿主文件或伪造自身测试。不要将不可信公开 benchmark 脚本当成已隔离执行。

工程增量是固定仓库版本、真实统一内核、受保护验收、预算贯穿摘要和最终留出测试、结果不自动激活。它不是新提出的优化算法或论文效果复现。

**严禁重复造不必要的轮子。** 执行、模型、JSON、哈希和存储一律先复用已有模块与 .NET 组件。新增自实现须写明现有方案的具体缺口；不得以研究为由绕过审批、预算或数据分割。
