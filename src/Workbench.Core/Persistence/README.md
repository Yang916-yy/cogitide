# 会话与事件持久化

[SqliteSessionStore.cs](SqliteSessionStore.cs) 是 `ISessionStore` 的 SQLite 实现：保存会话、消息与 checkpoint，追加按会话排序的事件，处理 schema 版本及恢复所需的持久状态。未知的新 schema 会拒绝打开，不静默降级覆盖。

复用 **Microsoft.Data.Sqlite 10.0.14**、SQLite 事务/WAL、参数化命令，以及 .NET 的 JSON、文件路径、异步取消和 `SemaphoreSlim`。它是对成熟数据库的应用适配，不是新的数据库、事件日志引擎或网络存储服务。已解析依赖由 [项目文件](../Workbench.Core.csproj) 与 [依赖锁](../packages.lock.json) 固定。

会话快照和原始事件承担不同职责：摘要只是可替换投影，原始消息仍保留；重启不得因为摘要或事件里出现一条命令就自动重放。存储成功和用户任务成功也分别判断。保存的代码、附件、工具结果和原生模型状态可能含用户内容，不属于可提交到公共仓库的测试产物。

本层没有声称采用新的学习算法；它为 [Runtime](../Runtime/README.md) 和 [Memory](../Memory/README.md) 提供可核对来源和恢复基础。当前部分查询会加载所选事件集合，不宣称端到端恒定内存或数据库检索已完全分页。

**严禁重复造不必要的轮子。** 新模块必须优先复用此 store 和 SQLite 的事务、索引、迁移能力，不得另写 JSON 数据库、重复事件日志或自行实现锁管理协议。确需新存储组件时，PR 必须给出测得的瓶颈、兼容迁移、恢复测试和成熟替代方案比较。
