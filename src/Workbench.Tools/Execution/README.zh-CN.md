[English](README.md) | **简体中文**

# 后台命令会话

[CommandSessions.cs](CommandSessions.cs) 提供 `start_command`、`read_command` 和 `stop_command` 的拥有者绑定、日志游标、状态及清理。每个命令由现有 Host 的隐藏 worker 执行；父 stdin 管道关闭代表所有权丢失，会请求取消所属 shell 和进程树。

会话 ID、短等待后返回、轮询、运行态/终态和有界输出的**行为参考 Codex commit `788b9d7bf6e37f735647b7f84b556c2a807df4d8` 的 unified exec**。文件映射、Apache-2.0 LICENSE/NOTICE 和未移植范围见 [Commands provenance](../Licenses/Commands/PROVENANCE.md)。没有调用 Codex 可执行文件，没有把 Rust 运行时或沙箱翻译进应用。

真正的进程与流由 .NET `Process`、`FileStream`、`CancellationToken`、`SemaphoreSlim`、`ConcurrentDictionary` 和 `System.Text.Json` 源生成提供；复用 [WorkspaceService](../Workspace/README.md) 的命令执行与编码处理。父管道所有权和日志恢复策略是 Cogitide 的工程组合，不是论文训练算法。

当前命令非交互、属于本次 run；取消、run 结束或 Host 丢失后停止。后续同一聊天可以读取日志，但不按旧 PID 接管、不自动重放。日志有明确预算与 UTF-8 字节游标；任务不能因为拿到了运行中的 ID 就被视为验证完成。进程树清理不是 OS 沙箱，也不保证捕获刻意脱离树的后代。用户交互终端使用 [Desktop 的成熟 PTY 组件](../../Workbench.Desktop/README.md)，不与模型命令混用。

**严禁重复造不必要的轮子。** 不得新增手写 PTY、Windows/Unix 终端模拟器、重复进程运行器或无限日志服务；优先复用 BCL、本模块及已有 PTY 库。必要变更须记录上游版本、取消/父进程丢失/Unicode/输出预算的实测证据，不能用“后台”作为绕过拥有者和终态检查的理由。

## Windows 快照替换

已有命令状态文件使用 BCL `File.Replace`，首次写入用无覆盖 `File.Move`。固定读者只共享 Delete 的 Windows 场景中，Move(overwrite) 会拒绝访问而 Replace 能替换路径并保留旧读者快照；参见 [dotnet/runtime #114230](https://github.com/dotnet/runtime/issues/114230)。真实 worker 回归持有旧快照直至完成，确认命令只执行一次、新旧视图均完整。Windows 替换期间打开路径可能短暂遇到文件缺失或共享冲突：读取仅针对这两种已观察错误、在目录仍存在时进行总计最多 310ms 的可取消重开；永久缺失和其他错误仍抛出。不承诺路径在内核替换的每个瞬间都可打开，也不重试命令或把失败当成功。阶段诊断只含固定阶段名、异常类型和 HRESULT，不包含命令、路径或凭据。
