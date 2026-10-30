[English](README.md) | **简体中文**

# Core：统一 agent 内核

所有模型进入同一个 [Runtime](Runtime/README.md)。[Models](Models/README.md) 负责成熟 SDK 适配，[Persistence](Persistence/README.md) 负责 SQLite；这两部分优先复用现成实现。规则在 [Projects](Projects/README.md)，证据与经验在 [Memory](Memory/README.md)，自动摘要与指南在 [Context](Context/README.md)。可选研究搜索与预取在 [Research](Research/README.md)。共享接口位于 Protocol，具体实现按上述职责组织。

固定上游与原始许可证在 `Licenses/`。核心论文机制、项目增加部分、未实现部分分别写在上述模块中；论文引用不是性能保证。默认行为和研究选项共享同一个工具/审批/存储路径，不建立平行 agent。

**严禁重复造不必要的轮子。** HTTP/SSE、JSON、数据库、密码学和调度先复用现有 SDK 与 .NET 组件；必要自实现须说明缺口、替代方案和验证。文件统计见 [模块清单](../../docs/module-map.md)。
