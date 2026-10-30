# C# / .NET 11 Coding Agent：平台与工程选型

核验日期：2026-10-30。范围是调研与实施建议；本轮没有安装 SDK、构建原型、运行性能基准或验证三平台二进制。下文的推荐属于工程判断，版本、接口和支持范围附一手来源。

## 建议决策

采用 **C# 15 / .NET 11 + Avalonia 12.1.x** 作为桌面主线，先锁定已公开的 SDK 和 NuGet 版本。产品以编码为主：读仓库、修改文件、运行测试、检查 diff、管理任务和终端；普通 work 首先覆盖资料检索、文件整理和内容产出。应用拥有自己的会话、事件、执行授权和恢复模型；通过适配层复用 `Microsoft.Extensions.AI`、MCP 官方 C# SDK，以及值得使用的 Microsoft Agent Framework 组件。

此版本按用户最新选择收敛为 **coding-first + 少量高频 work**。首轮不要求控制整个操作系统，不自建完整 IDE，也不把办公套件、全桌面视觉自动化和复杂多 agent 编排塞入基础版本。C# 可以减少网络、并发、进程管理、序列化、依赖注入等基础设施代码；实际竞争力仍需由任务完成率、恢复能力和交互来证明。不能从运行时微基准推导“agent 整体比 Python/TypeScript 快若干倍”，因为模型请求、浏览器和外部工具通常占据大量时间。

## 已确认的 .NET 11 状态

官方当前下载页列出的版本是 **.NET 11 RC3，2026-10-25 发布，SDK 完整版本 `11.0.100-rc.3.26522.148`**，标记为 Go-live；仍不是 GA。官方概览写明预计 2026 年 11 月 GA。可以按用户意愿直接以 `net11.0` 开发，但应以 `global.json` 固定完整 SDK，在 GA 后经过兼容性检查升级，而不是用浮动 `latest`。[下载页](https://dotnet.microsoft.com/en-us/download/dotnet/11.0) · [官方概览](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview)

RC3 包含官方发布的安全修复，因此新环境应以 RC3 而非 RC2 为基线；自包含发布需要重新构建才能携带更新后的运行时。[RC3 发布说明](https://devblogs.microsoft.com/dotnet/dotnet-october-2026-oob-security-updates/)

| 特性 | 对这个产品的用途 | 使用边界 |
|---|---|---|
| C# 15 `union`、`closed` hierarchy | 表达模型增量事件、工具执行结果、审批状态，帮助编译器检查分支完整性 | 首先用于内部强类型模型；持久化/IPC 仍用显式版本化 discriminator，避免语言表示变化破坏旧数据 |
| Collection expression arguments | 明确集合容量、比较器，少写初始化模板代码 | 优先可读性，不为了新语法改写全部代码 |
| Runtime Async | 改善大量异步模型流、工具调用和 IPC 的运行时开销与调用栈 | 用实际工作负载测分配量、CPU、延迟；不是任务质量提升手段 |
| 新 Process API、signal/exit status | 减少进程捕获、退出状态和 Unix 信号管理胶水 | PTY、整个进程树终止、沙箱仍需平台适配；普通进程重定向不等于交互终端 |
| System.Text.Json 新增/扩展的 union、JSONL 与序列化契约支持 | 事件传输、日志导出和协议 DTO | JSON source generation 本身是已有能力，11 是扩展；对事件日志做 schema 版本和迁移，JSONL 不是事务数据库替代品 |
| NativeAOT 改善 | 评估启动速度、常驻内存，尤其小型 worker/辅助进程 | 按组件采纳，先不把全应用 AOT 设为上线门槛 |
| Memory-safety 新规则 | 将来审查底层互操作层 | 仍是显式 preview；不为 agent 业务层增加 unsafe 代码 |

语言特性已经出现在当前官方文档，除 memory safety 外不需 `LangVersion=preview`；不能把它们继续描述成尚未实现的提案。[C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15)

`async/await`、`CancellationToken`、`Channel<T>`、依赖注入、JSON source generation、NativeAOT 都不是 .NET 11 首次提供的功能。它们是可继续复用的成熟基础；本文提到的 Runtime Async、JSON 扩展和 AOT 改善才是本次版本相关变化。文档支持 `net10.0` 的依赖通常可以成为 .NET 11 项目的候选，但这不等于已验证全部组合、源生成器和原生库兼容。

运行时选型还有硬件边界：.NET 11 的 x86/x64 最低指令集提高到 x86-64-v2；Windows Arm64 基线提高到含 LSE。应把最低 CPU 和 OS 版本写入发行要求，不承诺所有旧电脑。[运行时变化](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)

## 图形界面比较

“C# 有成熟桌面 UI”成立，但“.NET 自带一套官方统一的 Win/Linux/macOS 原生控件”不成立。原生进程、原生窗口、原生系统控件和自绘 UI 是不同层面。

| 方案 | Win / Linux / macOS | 渲染和优势 | 对本项目的判断 |
|---|---|---|---|
| Avalonia 12 | 都支持；12.1 增加可选择的原生 Wayland 后端，默认仍可走 X11/XWayland | 框架绘制控件，适合统一复杂桌面布局；可做平台互操作 | **首选**。需要验证中文输入、富内容、浏览器嵌入和系统集成 |
| Uno Platform | 官方支持三平台 Skia Desktop | WinUI/XAML 模型，若后续同时做 Web/mobile，有统一价值 | **备选**。当前支持文档使用 `net10.0-desktop`；.NET 11 工具链需验证，不能只改 TFM 假定工作 |
| .NET MAUI / MAUI Blazor Hybrid | 官方包含 Windows、Mac Catalyst、移动平台，没有 Linux 桌面 | 原生平台控件/平台 WebView，移动应用路线完整 | Linux 是硬需求时不宜作为统一主 UI |
| WPF / WinUI 3 | Windows | Windows 生态成熟，系统集成直接 | 适合 Windows 专用组件；不作为跨平台主 UI |
| Electron + .NET worker | 都支持 | Chromium/Node + Web UI 生态，复杂 HTML/编辑器集成方便 | 可作对照原型；引入第二套运行时和前端栈，应以实测体积、内存和开发量决定 |
| 自建 Blazor + 跨平台 WebView 壳 | 由壳决定，Blazor 本身不能解决平台缺口 | C# 业务层与 Web 富内容组合 | 若产品主体变成网页应用可再评估；现阶段会把 WebView 兼容性提前变成核心依赖 |

来源：[Avalonia Linux](https://docs.avaloniaui.net/docs/platform-specific-guides/linux) · [Uno 支持平台](https://platform.uno/docs/articles/getting-started/requirements.html) · [MAUI 支持平台](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms) · [WPF](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/) · [WinUI](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/) · [Electron](https://www.electronjs.org/docs/latest/)

Avalonia 当前 release 列表标记 **12.1.4（2026-10-09）** 为 latest；推荐在首次 spike 中固定此版本或实施时重新核验后的修订版。[12.1.4 release](https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.4)

### Avalonia 的关键核验点

- **无障碍：** 当前 12.x 文档列出 Windows UIA、macOS NSAccessibility、Linux AT-SPI2 全支持；这是框架能力声明，不能替代我们自己的聊天、时间线、弹窗、虚拟列表的键盘和屏幕阅读器测试。旧 XPF/v11 页面仍可能呈现不同状态，不能混用版本。[无障碍文档](https://docs.avaloniaui.net/docs/app-development/accessibility)
- **WebView：** 官方已宣布 Avalonia 12 中开源 WebView；当前包名为 `Avalonia.Controls.WebView`，使用系统 WebView2/WebKit 路线。Linux 文档的 WPE/WebKitGTK fallback 叙述仍有页面间差异，第一次 spike 必须实际测试指定包和系统依赖组合。不能把旧 v11 的“Linux 无内嵌 WebView”当作 12.x 结论，也不能因此承诺每个发行版零依赖运行。[开源公告](https://avaloniaui.net/blog/the-avalonia-webview-is-going-open-source) · [嵌入文档](https://docs.avaloniaui.net/docs/app-development/embedding-web-content) · [FAQ](https://docs.avaloniaui.net/tools/faq)
- **授权：** 框架是 MIT，可以用于商业应用；IDE 工具、部分高级组件、支持服务有单独许可。选 Markdown、PDF、图表等控件时分别核对，不把“框架免费”扩展成“所有组件免费”。[官方 FAQ](https://docs.avaloniaui.net/tools/faq)
- **AOT：** 采用编译绑定与 JSON source generation 使架构保持 AOT 友好；动态装载程序集和 Reflection.Emit 等机制与 NativeAOT 有冲突。插件优先通过独立进程/MCP 接入。[Avalonia XAML 编译](https://docs.avaloniaui.net/docs/xaml/compilation) · [.NET NativeAOT 限制](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- **Uno 备选的未决点：** 原生控件嵌入文档仍标记 Skia macOS 为“截至 6.0 尚不支持”；这条历史限制不应被外推成所有新版本仍不支持，但足以要求一次真实验证，尤其是浏览器与复杂文本编辑。[Uno 原生嵌入](https://platform.uno/docs/articles/features/using-skia-hosting-native-controls.html)

## Agent 基础库：复用什么，自持什么

| 层 | 建议 | 当前确认的事实与理由 |
|---|---|---|
| 模型调用 | `Microsoft.Extensions.AI.Abstractions` / `Microsoft.Extensions.AI`，外包一层能力描述与 provider adapter | 有 `IChatClient`、流式多模态消息、嵌入、遥测等。保留 provider 原生扩展，避免把 reasoning、缓存、结构化输出等差异压平 |
| Agent 框架 | 用 Microsoft Agent Framework 做对照实现和可选适配器，不让 UI/session schema 依赖它 | MAF **1.0 已于 2026-04-02 GA**，不应再按 2025 preview 判断。其 harness 已提供 compaction、skills、memory、审批和后台 agent 等能力，可以实测减少多少代码 |
| MCP | 官方 C# SDK `ModelContextProtocol.Core` / 必要时完整 hosting 包 | **v2.0 已于 2026-07-28 发布**，支持新版协议并保留向下协商；Apps/Tasks 分包。实验性旧 Tasks 有迁移例外，集成时锁版本并测旧服务 |
| Semantic Kernel / AutoGen | 现有应用迁移或特定 connector 才增加依赖 | 新项目不同时叠加三个编排框架；MAF 已承接二者融合方向 |
| 执行与恢复 | 自有 `RunCoordinator`、`ToolBroker`、版本化事件、检查点 | 用户停止、外部副作用、崩溃恢复、审批有效期、预算和产物关联是产品契约，需要自己明确语义 |

来源：[Microsoft.Extensions.AI](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) · [MAF GA 与 harness](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-at-build-2026-announce/) · [MCP SDK v2](https://devblogs.microsoft.com/dotnet/announcing-v20-of-the-official-mcp-csharp-sdk/) · [MCP C# 仓库](https://github.com/modelcontextprotocol/csharp-sdk)

稳定包不代表所有类型都稳定。当前 `IImageGenerator`、部分 routing/failover client 等仍标 experimental；可以隔离实验，但不要成为核心持久化或扩展接口。[AI 文档](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai) · [Routing/Failover 发布说明](https://devblogs.microsoft.com/dotnet/routing-and-failover-for-microsoft-extensions-ai/)

推荐先写一份相同任务、相同模型、相同工具的测试驱动，比较 MAF harness 与小型自有执行循环。测功能缺口、恢复语义、可观察性和维护代码量，再决定核心调用循环是否复用 MAF；不因为“自研”而把已有稳健实现全部重写。

## 建议模块和依赖边界

```text
Desktop (Avalonia, MVVM, virtualized timeline, artifact views)
    -> Application (session/run API, cancellation, notifications)
    -> Runtime (planning, budget, context, verification, checkpoints)
    -> ToolBroker (schema validation, capability grants, lifecycle)
         -> MCP worker / browser worker / process and PTY host
    -> Providers (IChatClient + provider-specific capabilities)
    -> Storage (SQLite + artifact files + versioned events)
```

这是逻辑分层，不要求第一天全变成微服务。首版可以是桌面进程、工具 worker 两类进程；运行时接口先允许将来移到独立后台 host。`CancellationToken` 贯穿执行路径，用 bounded `Channel<T>` 处理事件背压，UI 合并高频 token 更新。不要每个 token 刷新整个聊天列表。

依赖建议：Avalonia Desktop/Theme、CommunityToolkit.Mvvm、Microsoft.Extensions.Hosting/Logging、Microsoft.Extensions.AI、官方 provider SDK、MCP C# SDK、Microsoft.Data.Sqlite、OpenTelemetry。浏览器能力按需使用 Microsoft.Playwright；全文索引先考虑 SQLite FTS，向量检索等有任务证据再引入。除已核验的 SDK/Avalonia 外，具体包版本在实施首日从官方 NuGet/release metadata 核验并锁定，当前文档不虚构版本清单。

浏览器预览和浏览器执行分别设计。嵌入 WebView 用于呈现报告/文档/有限网页内容；受控浏览器 worker 用 Playwright 的独立 profile/context，记录动作和产物。Playwright .NET 支持三平台，但会带来浏览器二进制与系统依赖，不能因 GUI 自绘就声称整套应用没有浏览器体积。[Playwright .NET](https://playwright.dev/dotnet/docs/intro) · [浏览器依赖](https://playwright.dev/dotnet/docs/browsers)

## P0 的终端、代码和 diff 组件

终端需要分成三层：**进程生命周期、PTY 连接、终端仿真/渲染**。普通 `System.Diagnostics.Process` 的 stdout/stderr 重定向不提供 PTY；一个能画 ANSI 颜色的控件也不代表能正确运行交互 shell。P0 同时提供非交互工具日志和可接管的真实终端，二者共用任务标识与停止逻辑，界面分开呈现。[Windows ConPTY](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)

| 候选 | 能力与许可证证据 | 采用建议与未决点 |
|---|---|---|
| **Porta.Pty** | .NET PTY，Windows ConPTY、Linux/macOS 原生 C shim 调 `forkpty/openpty`；MIT。已读 `IPtyConnection.cs` 和 `.csproj`，当前 main 的项目版本 2.2.3、目标 net10.0 | **首选验证的 PTY 层**。`Resize`、读写流、退出事件可复用；Unix 原生库 RID、Windows ConPTY 分发、取消和进程树清理必须实测；main 版本号不等于已核实的 NuGet 发布版本 |
| **Iciclecreek.Avalonia.Terminal + XTerm.NET** | 原生 Avalonia 终端控件 + headless VT/ANSI 仿真，二者 MIT。当前 terminal main 的项目版本 4.1.1，引用 Avalonia 12.0.4、Porta.Pty 2.2.3、XTerm.NET 3.0.0 | **首选验证的原生渲染路线**。README 声明三平台，不将其自动视为生产质量；验证 CJK/emoji 宽度、IME、alternate screen、粘贴、滚动、vim/tmux 和终端尺寸变更 |
| **xterm.js + 自有 C# PTY bridge** | xterm.js 为 MIT，只负责浏览器侧终端，不负责创建 PTY | 原生控件未通过验证时的**备选**。可继续用 Porta.Pty 作后端，不强制引入 Node；代价是 WebView/JS 桥接和三平台浏览器渲染验收 |
| **node-pty** | MIT；真实三平台 PTY，Windows 使用 ConPTY，Unix `forkpty`；README 列 VS Code 等使用者 | 成熟的对照与故障排查参考；只有确有收益再作为 sidecar 引入，避免为一个能力默认携带第二运行时 |
| **AvaloniaEdit** | MIT；文本编辑、TextMate 高亮、折叠、行虚拟化、多光标等；当前 main 的中央包配置引用 Avalonia 12.0.0 | **首选代码预览和轻量编辑**。P0 做文件查看、定位、搜索、少量手动修正和“在现有 IDE 打开”；不承诺完整语言服务/调试器。验证大文件、长行、CRLF/LF、中文输入和源文件编码 |
| **DiffPlex** | Apache-2.0；.NET 文本 diff 算法，可生成 inline/side-by-side 模型；其仓库 UI 示例包含 WPF/WinForms/Blazor，不等于现成 Avalonia 组件 | 适合非 Git 文本比较和 Avalonia 自有 diff 展示的数据层。Git 工作区真实变化以 Git 输出为准，不能用 UI diff 算法替代 Git 索引、rename 和 patch 语义 |
| **Monaco Editor** | MIT；浏览器代码编辑器，包含 diff editor 能力 | 富编辑和 diff 的备选；需 WebView + JS 资源/worker + 本地内容桥接。只在 AvaloniaEdit 无法满足实际验收时引入，不同时维护两套编辑器 |

源码/许可：[Porta.Pty README](https://github.com/tomlm/Porta.Pty) · [PTY 接口](https://github.com/tomlm/Porta.Pty/blob/main/src/Porta.Pty/IPtyConnection.cs) · [PTY 项目](https://github.com/tomlm/Porta.Pty/blob/main/src/Porta.Pty/Porta.Pty.csproj) · [PTY MIT](https://github.com/tomlm/Porta.Pty/blob/main/LICENSE) · [Avalonia 终端项目](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/blob/main/src/Iciclecreek.Avalonia.TerminalWindow/Iciclecreek.Avalonia.Terminal.csproj) · [终端 MIT](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/blob/main/LICENSE) · [XTerm.NET MIT](https://github.com/tomlm/XTerm.NET/blob/main/LICENSE) · [xterm.js MIT](https://github.com/xtermjs/xterm.js/blob/master/LICENSE) · [node-pty](https://github.com/microsoft/node-pty) · [node-pty MIT](https://github.com/microsoft/node-pty/blob/main/LICENSE) · [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) · [编辑器 MIT](https://github.com/AvaloniaUI/AvaloniaEdit/blob/master/LICENSE) · [AvaloniaEdit 包配置](https://github.com/AvaloniaUI/AvaloniaEdit/blob/master/Directory.Packages.props) · [DiffPlex](https://github.com/mmanela/diffplex) · [DiffPlex Apache-2.0](https://github.com/mmanela/diffplex/blob/master/License.txt) · [Monaco](https://github.com/microsoft/monaco-editor) · [Monaco MIT](https://github.com/microsoft/monaco-editor/blob/main/LICENSE.txt)

上述主项目许可已核验；交付时仍应由依赖锁文件生成组件清单，检查原生二进制、字体、TextMate grammars 和其他转移依赖各自的许可。没有执行任何终端 demo，当前结论是可测试候选，不是实测选型成功。

**PTY 特别需要防止两个误判：** `ReadAsync` 带 token 不保证底层阻塞读取可取消；Porta.Pty 当前接口就显式区分 `SupportsCancellableRead`。`Kill()` 也不自动证明所有孙进程已清理。应用应由 `IProcessHost` 持有会话，关闭 tab 与停止任务分别定义语义，验证 shell 派生构建器、后台子进程和端口占用能按预期结束。

**Git 和文件变更：** 首轮优先调用现有 Git CLI，命令参数以 argv 传递，展示 status/diff/checkpoint/worktree；补丁应用前核对基础文件 hash，发现用户编辑后重新计算。先做可审阅的统一 diff、接受/拒绝和打开编辑器，再扩展复杂的三方合并。工作区外的高频 work 复用同一文件、浏览器和产物系统。

## 后置：全桌面自动化

P0 不依赖 UIA/AX/AT-SPI 去操控任意第三方应用，不以系统截图和坐标点击作为编码主路径。浏览器采用 DOM/Playwright，文件和 Git 走明确接口；自己的 UI 仍要做好无障碍。

将来若真实用户任务需要全桌面控制，再单独增加 `IDesktopAutomation` 和 `IScreenCapture`：Windows UIA、macOS Accessibility/ScreenCaptureKit、Linux AT-SPI 与 Wayland portals 各自实现。macOS 屏幕捕获/自动化权限、Wayland RemoteDesktop 的会话授权都属于后续范围，不作为首轮交付依赖。[Apple ScreenCaptureKit](https://developer.apple.com/documentation/screencapturekit) · [Apple 权限模型](https://support.apple.com/en-ca/guide/security/secddd1d86a6/web) · [XDG RemoteDesktop](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.RemoteDesktop.html)

## 首轮实施验证门槛

先按以下三个验证批次推进，不承诺几天内完成全部三平台集成。没有 Mac/Linux 真机、签名证书或出现终端/输入法兼容问题时，耗时会显著增加；第一批实测后再估后续工作量。

1. **批次 A：编码桌面基础。** 固定 SDK/Avalonia/编辑器/PTY 候选，在 Windows、真实 macOS、Linux 各发布运行。测试打开仓库、搜索文件、编辑/展示 diff、执行构建/测试、真实 shell 的 Ctrl+C/尺寸变化/交互、停止进程树、中文 IME、DPI 与拖放。至少覆盖 macOS arm64、Windows x64、Linux x64；其他架构按产品支持范围追加。
2. **批次 B：任务闭环与恢复。** 一个真实修改任务贯穿模型流、读写文件、Git diff、测试、检查点和用户审阅。注入工具超时、用户同时改文件、取消、模型断流、应用重启、重复响应和部分结果。10,000 条消息、大 diff/长行、流式工具输出用于检查虚拟化、背压和内存；记录启动 p50/p95、RSS、CPU 与交互延迟。MAF 对照使用相同任务和工具契约。
3. **批次 C：浏览器与少量 work。** 在三平台验证受控浏览器的网页读取、下载、前端应用测试与产物预览。若使用嵌入 WebView，再验证系统依赖、OAuth 回跳与长页面；其不稳定时先用外部浏览器，不阻断核心编码闭环。随后完成签名/公证、安装升级回滚和干净机器验收；Linux 桌面窗口与输入测试仍保留，但不要求全桌面控制 portal。

CI 在三类 OS 上构建；真实桌面的输入、终端与发布安装测试是单独验收项。交叉编译成功、headless 单测通过都不能代替这些验收。

若 Avalonia 的指定平台组合不能通过中文输入和富内容测试，再用相同页面与任务测 Uno 或 Web 壳；不要只凭截图美观更换架构。若 AOT 阻碍 provider/工具生态，先发布自包含 JIT 版本，保留 AOT worker 实验。

所有跨平台性能、原生集成和框架组合兼容性仍待上述实测。本轮证据足以确定优先验证的路线，不足以宣称已经达到 Pi/Codex 或其他竞品的稳定性。
