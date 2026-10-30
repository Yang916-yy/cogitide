# .NET 原生能力与组件复用审查

核查日期：2026-10-30。针对当前 Cogito 工程；正式名称未定。结论是继续使用 **BCL + 厂商 SDK + 少量成熟组件**，把自有代码集中在任务状态、审批、上下文和可恢复执行。下文区分已经使用、产品必需封装和暂缓采用的特性，不将版本升级本身当作性能实测。

## 已采用的实现

| 领域 | 当前实现与位置 | 自有代码负责什么 |
| --- | --- | --- |
| HTTP、TLS、连接池 | .NET `SocketsHttpHandler`、`HttpClient`；[ModelHttpTransport.cs](../src/Workbench.Core/Models/ModelHttpTransport.cs) | 固定连接寿命、空闲回收、连接超时、响应头预算、压缩和禁止自动重定向的应用策略；不实现 socket、TLS 或 HTTP 协议 |
| 模型请求与 SSE | 官方 OpenAI .NET SDK 2.14.0，System.ClientModel 1.15.0；[OpenAiCompatibleModel.cs](../src/Workbench.Core/Models/OpenAiCompatibleModel.cs) | 厂商参数适配、完整结束标记检查、工具调用聚合与总预算、取消和可安全保存的错误；不自写 SSE 协议解析器 |
| JSON 协议与持久化 DTO | System.Text.Json 原生 source generation；[WireJsonContext.cs](../src/Workbench.Protocol/WireJsonContext.cs)、[Contracts.cs](../src/Workbench.Protocol/Contracts.cs) | 稳定契约使用生成的 Metadata；匿名工具载荷保留反射 fallback，维持现有 JSON 兼容性 |
| 工具注册 | BCL `FrozenDictionary`；[AgentEngine.cs](../src/Workbench.Core/Runtime/AgentEngine.cs) | 固定工作区工具只构建一次；每轮绑定会话专属的 `inspect_evidence`，按 Ordinal 查找并拒绝保留名称冲突 |
| Git 清单与忽略规则 | 用户安装的 Git CLI；[RepositoryInventory.cs](../src/Workbench.Tools/Navigation/RepositoryInventory.cs) | 用 `git ls-files -z --cached --others --exclude-standard -- .` 获取标准 Git 语义，再做路径边界、资源预算和分页；不重写 gitignore 解析器 |
| 文件 glob 与文本检索 | Microsoft.Extensions.FileSystemGlobbing **10.0.14**、BCL `Regex` / `Span`；[NavigationTools.cs](../src/Workbench.Tools/Navigation/NavigationTools.cs) | `Matcher.Match` 只过滤已验证的清单；正则显式开启并设置每行匹配 100 ms 超时，默认仍为大小写不敏感的字面检索；不再写 glob/regex 解析器 |
| C# 代码结构 | Microsoft.CodeAnalysis.CSharp 5.9.0；[CSharpSymbols.cs](../src/Workbench.Tools/Navigation/CSharpSymbols.cs) | 用 Roslyn 语法树提取声明、签名、行号和语法诊断；不写正则 C# 解析器，不加载项目、MSBuild、生成器或仓库分析器 |
| 多文件补丁 | OpenCode 固定版本局部 C# 移植 + BCL 文件 I/O、SHA-256、取消；[PatchTools.cs](../src/Workbench.Tools/Editing/PatchTools.cs) | 保留补丁格式及有序 hunk/replacement 思路，接入已有锁、路径检查和哈希前置条件；自有集成负责预检、备份、提交及受控失败回滚 |
| 后台命令会话 | Codex unified exec 行为适配 + BCL `Process`、重定向流、`SemaphoreSlim`、`ConcurrentDictionary`；[CommandSessions.cs](../src/Workbench.Tools/Execution/CommandSessions.cs) | 隐藏 Host worker、所属聊天/工作区、父管道失联取消、运行/终态区分、日志游标及资源预算；没有引入另一套进程库、Codex 可执行程序或 Rust runtime |
| Skills 元数据 | YamlDotNet 18.1.0；[ProjectRules.cs](../src/Workbench.Core/Projects/ProjectRules.cs) | Pi 发现逻辑的 C# 改编使用成熟 YAML DOM，仅提取标量元数据；文件读取、SHA-256、生成正则、JSON 源生成使用 BCL |
| 摘要与经验 | Pi/OpenCode 局部移植 + 现有 SQLite 事件库；[来源映射](../src/Workbench.Core/PROJECT-REUSE.md) | 有界摘要投影、源事件核对与条件化经验小条目；不新增向量数据库、后台记忆服务或第二套网络栈 |
| 会话存储 | Microsoft.Data.Sqlite 10.0.14；[SqliteSessionStore.cs](../src/Workbench.Core/Persistence/SqliteSessionStore.cs) | 产品会话、事件顺序和恢复规则；暂不增加 ORM 或第二套数据库抽象 |
| 多语言 | BCL `.resx`、`ResourceManager`、`CultureInfo`；[Localization.cs](../src/Workbench.Desktop/Localization.cs) | 简体中文、英文、跟随系统和即时切换；Avalonia binding 刷新与偏好保存，不翻译用户、模型或工具原文 |
| 桌面与动画 | Avalonia 12.1.4、原生控件、`Animation` / `BrushTransition`；[UiMotion.cs](../src/Workbench.Desktop/UiMotion.cs) | 短时控件反馈和面板过渡、减少动画偏好、取消与脱离视图清理；不引入 WebView 或 JS 动画栈 |
| 代码、Markdown、终端 | AvaloniaEdit、Markdig、Iciclecreek.Avalonia.Terminal / Porta.Pty / XTerm.NET | 只读预览、安全渲染、用户显式打开终端及生命周期；不重写编辑器、Markdown 解析器或终端模拟器 |

Avalonia 是第三方跨平台 .NET UI 框架，**不是 .NET BCL 自带的三端 GUI**。本工程采用原生 Avalonia 控件渲染，不能据此声称 Windows、Linux、macOS 的所有行为已经一致。组件版本、许可证与边界见 [桌面组件记录](../src/Workbench.Desktop/DEPENDENCIES.md) 和 [仓库导航组件记录](../src/Workbench.Tools/DEPENDENCIES.md)；分发仍须保留适用的许可证声明。

## 网络栈审查

当前一个进程共享一个 BCL transport，连接寿命 5 分钟、空闲回收 2 分钟、连接超时 30 秒；SDK 请求网络超时为 5 分钟，并接受运行取消令牌。关闭 cookies 和自动重定向，避免模型请求意外转向另一地址。`MaxConnectionsPerServer = 16` 是连接限制，不是 HTTP/2 所有请求的并发总上限。采用长生命周期 HttpClient 并设置连接寿命符合 [Microsoft HttpClient 指引](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines)。

这是显式管理应用策略，**不是修复此前已经证实的连接池泄漏**。System.ClientModel 默认实现本来就共享 HttpClient，且默认禁用自动重定向，见 [官方 transport 源码](https://raw.githubusercontent.com/Azure/azure-sdk-for-net/main/sdk/core/System.ClientModel/src/Pipeline/HttpClientPipelineTransport.cs)。上游源码可能继续变化，本项目实际依赖由 NuGet lock 固定。

HTTP/SSE 仍由 [官方 OpenAI .NET SDK](https://github.com/openai/openai-dotnet) 处理；保留 `RetryPolicy(maxRetries: 0)` 是执行一致性决策：中断流、429 或 5xx 不会被静默重放。应用可以提供显式重试，但须先让用户核对已执行动作。上游错误正文、响应头与凭据不进入可持久化的 `ModelRequestException`；这不等于任意用户代码、工具输出都已经脱敏。

模型增强轮新增官方 **Anthropic C# SDK 13.3.0**，其 SDK 使用 BCL `System.Net.ServerSentEvents` 和官方 `MessageContentAggregator`。本地仅加完整流、工具与状态一致性检查；传输共用现有 `HttpClient`，`MaxRetries=0`、内容 telemetry 关闭。没有为新厂商再实现网络/SSE 栈。版本、固定源码与双许可证见 [Anthropic 记录](../src/Workbench.Core/ANTHROPIC.md)。图片序列化同样复用两家 SDK 的原生类型；聚合字节计数用 BCL `Base64.IsValid`，不重复解码。该 API 不被宣称为 .NET 11 首创。

证据召回使用 BCL `SHA256`、`Lock`、`FrozenSet`、System.Text.Json 源生成和既有 SQLite 事件库，没有增加向量库、图数据库或外部服务。它保证来源可核对与保守失效，不证明自然语言结论或完整任务成功；当前事件读取尚未分页。

新工具的证据适配保留多文件目标哈希及删除状态，重放时核对完整 artifact 列表；删除后重建、移动目标变化会使适用性检查失败。文件新鲜度检查用 `IncrementalHash` 和 16 KiB 读取缓冲，最多读取 16 MiB，不为哈希再整文件分配。后台命令启动、轮询和停止均保守推进失效 epoch；只有输入身份与完整终态一致的零退出结果能形成命令观察，运行、取消或失联不能冒充验证成功。项目经验保存前检查完整单条召回包能否装入 16,000 字符预算，避免保存后无法完整召回。以上仍只核对来源及报告的文件状态，经验始终是候选，不能自动推导可靠性。

无需为这一层再引入 RestSharp、手写 HTTP 客户端、私有 SSE parser 或另一套重试中间件。将来出现可测量的多厂商策略、代理或服务生命周期需求时，再评估 `IHttpClientFactory` / Microsoft.Extensions.Http；当前单进程共享 transport 已能覆盖实际用法。

## .NET 11 / C# 15 使用情况

工程固定 `11.0.100-rc.3.26522.148` SDK、`net11.0` 和 `LangVersion=15.0`，禁止自动 roll-forward。当前仍是预发布工具链。

- **明确采用 C# 15 特性**：`AgentEngine.RunOutcome` 使用 `closed record` 表达 Completed / Cancelled / Failed，并通过穷举 switch 产生最终状态。该特性使新增结果分支时编译器能检查处理遗漏；见 [C# 15 closed hierarchies](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15#closed-hierarchies)。
- **已用但并非 .NET 11 首创**：`IAsyncEnumerable`、`CancellationToken`、`WaitAsync`、`ArgumentList`、异步流 I/O、`FrozenDictionary`、`Regex` 超时、`IncrementalHash`、System.Text.Json source generation、ResourceManager，以及 Microsoft.Extensions.FileSystemGlobbing。这些同样值得复用，不应包装成新版本独有能力。
- **JSON source generation**：协议 DTO 与集合显式使用 Metadata 模式，保留原有 Web defaults、枚举与动态载荷兼容；新增命令 DTO 使用独立生成 context 的默认生成模式。未测量快路径收益，也没有移除全部反射或启用 Native AOT。官方说明见 [System.Text.Json source generation](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)。
- **框架升级收益待测量**：没有当前项目的 JIT、GC、吞吐、启动时间或内存基准，不能给出“降低多少代码量/提速多少”的数字。应先记录相同真实任务的耗时、分配和峰值内存，再决定进一步优化。

## 已核查但暂缓采用

以下 API 名称在固定 RC3 的 reference assemblies 元数据中存在；存在不代表本产品已经采用或验证其平台行为。在线的 [.NET 11 libraries 说明](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries) 仍标记基于 RC2，最终判断应以固定 SDK、实际编译与平台实验为准。

| 特性 | 当前决定及再次评估条件 |
| --- | --- |
| `Process.RunAndCaptureTextAsync` | 不直接替换模型命令/Git 管线。当前要求输出截断预算、NUL 分隔路径、实时事件、超时及进程树处理；一次性全量捕获不能直接保留全部约束。以后仅用于输出规模可控的一次性探测。 |
| `Process.ReadAllLinesAsync` | 不用于 NUL 分隔的 Git 清单。命令输出还需考虑无换行长行及总字符上限；有符合边界的逐行场景再替换。 |
| `ProcessStartInfo.KillOnParentExit` | 暂缓启用，后续先验证 Host 被强制终止、普通启动器/Job containment、子孙进程及各平台支持，再限定到应用拥有的工具子进程；不要无差别施加给浏览器授权或用户独立程序。 |
| `JsonSerializer.SerializeAsyncEnumerable(topLevelValues: true)` | 原生 JSONL 输出值得用于未来事件导出。当前 RPC writer 有逐条发送、flush、互斥和响应/通知交错要求，暂不为使用新 API 引入新的队列及退出语义。 |
| `SocketsHttpHandler.ShouldEvictConnection` | 固定连接寿命已满足当前需求；没有证据需要自定义逐连接淘汰逻辑。 |
| `SocketsHttpHandler.InitialHttp2MaxConcurrentStreams` | 尚无模型服务初始 HTTP/2 stream 限额故障证据；先观测再调参。它不能代替全局运行预算。 |
| Native AOT / 全量 trimming | 暂不开启。反射 fallback、Avalonia、第三方组件与发布行为要逐项审计；self-contained 发布不是 Native AOT。 |

`KillOnParentExit` 的官方资料存在版本差异：libraries 总览写 Windows only，较新的 [API 页面](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.killonparentexit?view=net-11.0) 标注 Windows/Linux/Android，均不能推导为 macOS 支持。另一个历史 Windows 包含关系问题 [runtime #134990](https://github.com/dotnet/runtime/issues/134990) 仅在 RC1 报告复现，目前已关闭并关联 #135032。**本审查没有复现 pinned RC3 的该问题，也不能把已关闭问题当作当前故障。** 暂缓的依据是尚未完成产品生命周期/平台验收。现有取消和正常退出仍使用 .NET 进程树终止；本轮新增独立 worker 监听父 stdin 管道 EOF 并取消所属命令。该实现没有启用 `KillOnParentExit`，不能据此保证任意刻意脱离进程树的子孙进程都被 OS 回收，三端生命周期行为仍需实机验证。

## 本轮工具改编的来源与实际边界

| 本地文件 | 固定上游与保留范围 | 许可证与来源记录 |
| --- | --- | --- |
| `NavigationTools.cs` | OpenCode `60072747957926e7aeec7e911fe6e23abe9bf830` 的 read/grep/glob 语义；零起点游标、已有工作区边界和 Git 清单仍由本工程决定 | [导航 provenance](../src/Workbench.Tools/Licenses/Navigation/PROVENANCE.md)，OpenCode MIT；globbing 10.0.14 的 Microsoft MIT 与原始第三方声明 |
| `PatchTools.cs` | OpenCode v1.19.5 `810c0eba1a7348036f8bdf61e6632d0c36c6876c` 补丁模块的局部 C# 移植；使用唯一精确上下文，不移植宽松空白/标点回退 | [补丁 provenance](../src/Workbench.Tools/Licenses/Patch/PROVENANCE.md)，原始 MIT 声明 |
| `CommandSessions.cs` | Codex `788b9d7bf6e37f735647b7f84b556c2a807df4d8` unified exec 的 ID/yield/poll/终态和截断语义；执行及父管道机制为独立 .NET 实现 | [命令 provenance](../src/Workbench.Tools/Licenses/Commands/PROVENANCE.md)，原始 Apache-2.0 LICENSE / NOTICE；保留 NOTICE 中 Ratatui 字样不代表本组件使用 Ratatui |

三个 provenance 文档提供精确源码链接和改编差异，许可证按 Navigation/Patch/Commands 子目录随 Tools 构建及发布复制。这里的“复用”有明确区分：globbing、Roslyn 是锁定 NuGet 依赖；补丁是局部源码改编；命令会话是行为契约适配，不能笼统称为 fork 了整个上游运行时。`CommandRecord` / `CommandLaunch` 用 System.Text.Json 源生成 metadata；匿名工具响应沿用 `Wire.Json`，没有声称消除了全部反射。

`read_file` 的上限扩大到原始 UTF-8 字节 16 MiB，页面最多 12,000 文本字符，整文件哈希和行/字符双游标用于后续页校验。当前先缓冲并解码这个有界文件，再选取页面；没有把分页包装成恒定内存或随机访问读取。搜索有 2,000 文件 / 32 MiB 扫描预算，100 ms 正则超时是单行匹配预算而非整个请求时限；搜索 snapshot 只覆盖已扫描内容，glob snapshot 只覆盖匹配文件名。`Matcher.Match` 使用 [Microsoft 官方的内存匹配接口](https://learn.microsoft.com/en-us/dotnet/core/extensions/file-globbing#match-overloads)，不另行遍历文件系统；不承诺 ripgrep 的全部表达式或 glob 扩展。

补丁仍保持每文件 1 MiB、64 个源操作、512 个 hunk、8 MiB 原始/替换字节合计及 80,000 字符结果预算。提交前检查全部原始哈希，受控失败进行反向回滚；断电/进程崩溃期间不保证多文件原子性，外部并发修改也不受普通路径 API 的事务保护。后台命令保留最多 4 MiB 日志，使用 UTF-8 字节游标，每次最多返回 12,000 文本字符；单 manager 最多 16 个活动任务，同一工作区最多四个。它是非交互执行，不提供 PTY、OS 沙箱、丢失 worker 的恢复重挂或自动重放。这些边界由产品需求决定，没有吞吐、内存、启动时间或代码量减少的实测收益声明。

## 保留的自有逻辑与下一步

审批前后状态检查、工具参数验证、工作区路径边界、SHA-256 编辑冲突保护、流完成条件、whole-record JSON 分页、持久化顺序和未知结果恢复都属于产品行为，不能仅用一个 SDK 调用消除。当前简单 agent loop 也承载这些规则；未来评估 Microsoft Agent Framework 或 MCP C# SDK 时，应以能否保留现有契约与故障恢复测试作为取舍依据。尚未接入它们，不能把计划写成已完成能力。

仓库导航已从固定小清单扩展为有预算的 Git 清单、分页搜索和 C# 声明定位。Roslyn 返回 `semanticAnalysis: false`；语法大纲不是类型检查、完整调用图或构建成功证据。非 Git 工作区的文件系统 fallback 明确提示未解析 gitignore；存在 Git 标记但 Git 无法检查时不回退扫描忽略文件。

对应验证入口包括 [provider 边界测试](../tests/Workbench.Tests/ProviderTransportTests.cs)、[原导航测试](../tests/Workbench.Tests/RepositoryNavigationTests.cs)、[导航升级测试](../tests/Workbench.Tests/NavigationUpgradeTests.cs)、[补丁测试](../tests/Workbench.Tests/PatchTests.cs)、[命令会话测试](../tests/Workbench.Tests/CommandSessionTests.cs)、[新工具证据与经验测试](../tests/Workbench.Tests/EvidenceToolUpgradeTests.cs)、[协议兼容测试](../tests/Workbench.Tests/ProtocolSerializationTests.cs) 与 [桌面本地化实际控件测试](../tests/Workbench.Tests/DesktopBindingTests.cs)；它们不能替代三端实机。最新通过数、集成和打包证据统一维护在 [工程状态](../docs/implementation-status.md)，不在此复制易过期的测试数字。

下一轮优先用 BCL `ActivitySource` / `Meter` 建立请求、工具、token 和资源基线，并用固定仓库任务评估现有摘要、证据与工具组合。三端实机按用户安排暂缓；不为勾选新特性而改变已经验证过的执行语义。
