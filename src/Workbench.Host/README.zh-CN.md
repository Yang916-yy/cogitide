[English](README.md) | **简体中文**

# 应用 Host 与显式研究入口

| 文件/目录 | 职责 |
| --- | --- |
| [Program.cs](Program.cs) | stdio 请求/事件、实例锁、工作区/会话所有权、账号与模型装配、统一 AgentEngine 调用、审批以及后台 command worker 入口。 |
| [Research/ResearchCommandLine.cs](Research/ResearchCommandLine.cs) | 用户显式启动的技能/压缩优化 CLI；配置校验、固定仓库任务的隔离评测和实际模型用量统计。 |

Host/客户端分离与请求/事件组织参考 Codex、OpenCode 等成熟 agent 的职责划分；当前 wire schema 和装配代码由本项目维护，**不是这些项目完整 app-server 的移植**。工具行为改编的固定来源分别保留于 [Tools provenance](../Workbench.Tools/DEPENDENCIES.md)，模型和鉴权使用 [官方 SDK 适配](../Workbench.Core/Models/README.md) 与 [Auth](../Workbench.Auth/Accounts/README.md)。

网络、进程、JSON、任务取消和同步复用 .NET；会话/事件使用现有 SQLite store。所有模型都调用同一个 [AgentEngine](../Workbench.Core/Runtime/README.md)。账号登录只提供鉴权，不启用外部 Codex agent。Host 持有运行资源和工作区锁，客户端断开或停止不能变成重放命令的理由。

研究 CLI 是显式批处理接口，普通聊天不会自动启动它。GEPA/SkillOpt 算法与报告属于 [SkillOptimization](../Workbench.Core/Research/SkillOptimization/README.md)，ACON 指南比较属于 [Context](../Workbench.Core/Context/README.md)；Host 只适配真实编码任务、固定验收和运行预算。产物保持候选身份，不自动安装或扩大权限。任务/模型/基线/预算必须记录，真实评测、协议夹具与论文复现分别陈述。

**严禁重复造不必要的轮子。** 新入口须复用 Core、Tools、Auth 和 Protocol，禁止在 CLI 再写 agent 循环、HTTP/SSE、数据库、进程执行或审批服务。确有独立适配层需要时，在 PR 解释现有接口缺口、成熟组件选择和运行生命周期测试；不得只为了复刻上游目录结构搬入无关依赖。
