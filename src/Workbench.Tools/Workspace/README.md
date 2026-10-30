# 工作区基础设施

[WorkspaceService.cs](WorkspaceService.cs) 提供统一路径边界、单文件读写/唯一文本替换、Git diff、程序/命令执行，以及各 partial 工具模块共用的锁、编码和结果边界。对外的工具注册在 [WorkspaceTools.cs](../WorkspaceTools.cs)；调用方不应另外拼一套文件访问策略。

基础设施复用 .NET `Path`、`FileStream`、严格 UTF-8、SHA-256、`Process`、异步重定向流和取消令牌。Git 状态/diff 使用 **Git CLI**，而不是自写 Git 对象、diff 或 ignore 解析器。具体依赖见 [Tools DEPENDENCIES](../DEPENDENCIES.md)。此文件以应用策略与 BCL 组合为主，不声称整段逐行移植自某个上游项目。

应用层额外约束包括：工作区外路径/链接拒绝、现有文件哈希检查、唯一匹配修改、输出预算、取消及进程清理。这些不是操作系统沙箱；shell 仍以用户权限运行，允许的命令也可能访问工作区之外。多文件补丁、导航和后台命令分别见 [Editing](../Editing/README.md)、[Navigation](../Navigation/README.md)、[Execution](../Execution/README.md)。

**严禁重复造不必要的轮子。** 不得新增重复的路径穿越判断、文件哈希、shell wrapper 或手写 Git/diff 实现；先扩展本服务和 .NET/Git 现成能力。必要例外必须在 PR 说明现有实现的具体限制、替代方案和并发/取消/编码验证，不能只以“更可控”作为理由。
