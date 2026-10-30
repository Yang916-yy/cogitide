# Tools：成熟工具契约的 C# 适配

`WorkspaceTools.cs` 汇总工具定义，[Workspace](Workspace/README.md) 处理路径与 BCL 文件/进程能力，[Navigation](Navigation/README.md) 复用 Git、Roslyn、正则与官方 globbing，[Editing](Editing/README.md) 聚焦移植 OpenCode 的 Codex 风格 patch 模块，[Execution](Execution/README.md) 使用 .NET 进程实现 Codex 启发的后台命令生命周期。

[RepositoryMemory](RepositoryMemory/README.md) 是例外的研究模块：按 RepoMem/DCI 思路检索冻结版本的仓库历史，并提供可核对的来源。它不替代当前源码阅读。

固定源文件、commit、改编范围与上游许可证在 `Licenses/`。**严禁重复造不必要的轮子。** 新工具先检查 BCL、现有工作区原语与成熟库；对新自实现说明实际缺口及验证，不因上游有功能而全量搬入不需要的运行时。
