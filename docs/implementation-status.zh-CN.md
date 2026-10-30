[English](implementation-status.md) | **简体中文**

# 工程状态与验收

更新：2026-10-30。内部开发代号 **Cogito**，来源于 Cogito, ergo sum；正式名称以后确定。编码优先，保留少量高频 work 的扩展位置。技术项目名、程序集与数据目录继续使用 Workbench。名称核查见 [决定记录](../research/naming.md)。

当前源码已统一为自有 `AgentEngine`：ChatGPT 官方登录、Responses API 密钥、Chat Completions、DeepSeek 和 Claude 都由同一内核执行工具、摘要与项目复用。本轮补齐分页读取、正则/glob、多文件补丁和后台命令，生产工具 17 项，只读模式 12 项；精确验证结果见 [工具升级验收](tools-upgrade-validation.md)。此前 272 项测试与包记录见 [统一内核验收](unified-kernel-validation.md)。真实 ChatGPT 登录/在线推理和 Linux/macOS 实机验收尚未完成。

## 已落地的结构

| 项目 | 职责 |
| --- | --- |
| Workbench.Protocol | 版本化 JSONL 请求、通知、会话、工具和模型契约 |
| Workbench.Core | 原生模型适配、流式工具循环、审批、自动摘要/checkpoint、SQLite 状态与事件、层级规则、skills 与项目经验 |
| Workbench.Tools | 工作区内文件、搜索、哈希校验修改、命令和 Git diff |
| Workbench.Auth | 官方动态注册 OAuth、账号切换、令牌续期、JWT 校验、系统保护存储和账号模型目录；不执行 agent 任务 |
| Workbench.Host | stdio 服务，实例锁、会话与工作区运行所有权，账号绑定，所有入口路由至同一个 AgentEngine |
| Workbench.Desktop | Avalonia 桌面、会话时间线、代码预览、diff、输出与连接设置 |
| Workbench.Tests | 流式协议故障、执行边界、持久化、工具、OAuth/Responses 夹具和真实 Desktop 的后台测试 |

GUI 与 Host 是独立进程。GUI 不能通过显示状态替代 Host 的真实执行结果。事件先持久化再发送；已执行工具不会因 UI 断开而自动重试。每个工作区只允许一个活动的 agent 会话；并行修改使用独立工作树。

界面采用简洁的侧栏与对话布局，文件、变更和输出在需要时展开。工具步骤默认收起参数和结果，错误仍明确呈现；审批区可滚动查看完整参数。助手消息使用 Markdig 解析并通过 Avalonia 原生控件呈现标题、列表、表格与代码块，代码块可以复制。过长或过于复杂的 Markdown 降级为有显示长度上限的可选纯文本，并明确提示截断。提交输入前释放当前草稿，失败时不会覆盖用户随后输入的新草稿。UI 名称集中在 `ProductIdentity`，便于以后更换正式品牌。

固定 SDK 为 `11.0.100-rc.3.26522.148`，目标 `net11.0`，C# 15。核心结果类型采用 closed hierarchy 与穷举匹配；流使用 `IAsyncEnumerable` 和取消令牌，JSON 使用 System.Text.Json，状态存 SQLite。当前不启用 Native AOT，也不宣称已有 .NET 11 性能收益实测。

已有简体中文/English/跟随系统，使用 .NET `ResourceManager` 与 `.resx` 卫星资源；界面标签即时刷新，用户、模型和工具原文保持不变。语言与“减少动画”偏好即时保存，API 配置仍由保存按钮确认。面板使用 Avalonia 原生 150 ms 过渡、控件使用 120 ms 反馈；减少动画可关闭这些效果。当前没有接入系统级减少动画偏好。

模型网络由官方 OpenAI / Anthropic .NET SDK 处理 HTTP 序列化和 SSE，显式共用 BCL `SocketsHttpHandler` 连接池并配置连接寿命、超时、无 Cookie、无自动重定向；OAuth 服务使用 BCL HTTP。模型请求不自动重放；错误以安全摘要持久化。协议稳定 DTO 使用 System.Text.Json 源生成元数据，匿名/动态对象保留反射回退，工具目录使用 `FrozenDictionary`。这些成熟能力不全部属于 .NET 11 新增特性，详见[原生能力与复用审查](../research/dotnet-reuse-audit.md)。

## 模型与账户

| 入口 | 当前方式 | 验证情况 |
| --- | --- | --- |
| DeepSeek | OpenAI 官方 .NET SDK；none/low/high/max；推理状态完整回放 | 历史真实工具修改、重启续接及独立视觉通过；混合任务视觉出现错误，见模型验收 |
| Anthropic Claude | 官方 Anthropic 13.3.0 SDK；原生 Messages、thinking、工具、图片 | 实际 SDK 协议夹具与 Engine/SQLite 重启测试通过；本机没有密钥，未真实调用 |
| ChatGPT | 官方动态注册 OAuth；OpenAI .NET SDK 直接调用公共 Responses API | OAuth、SDK、内核和 UI 后台测试通过；Windows DPAPI 实测通过；未替用户真实登录或调用账户模型 |
| OpenAI Responses API | API 密钥、官方 OpenAI .NET SDK ResponsesClient | 真实 SDK 序列化/SSE 夹具覆盖；完整本轮结果见统一内核验收 |
| 其他兼容 API | 配置 endpoint、model、密钥环境变量名 | 本地 SSE 契约测试通过；各厂商仍需分别验收 |

登录入口与 API 设置分列。用户点击登录后，Desktop 打开 `Workbench.Auth` 生成的官方授权地址；Auth 本身不启动浏览器。账号注册、稳定 host ID 与凭据独立保存，支持选择、添加、取消登录及退出；重新登录保留原注册。令牌使用 Microsoft 的系统存储组件保护，JWT 验证使用 Microsoft.IdentityModel；存储不可用时不回退明文。协议来源为 [官方账号与会话](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions)。Workbench 不提取已有 ChatGPT/Codex 凭据，也不依赖 Codex CLI。自定义 API 只接受 HTTPS，loopback 本地模型允许 HTTP。

ChatGPT 推理地址固定为 `https://api.openai.com/v1/responses`，使用 `store:false` 和显式输入历史，完成事件通过后才提交输出与工具。Host 在首次执行时将会话绑定到账号注册，每次模型请求前重新取得凭据并核对绑定；账号切换需要新聊天。模型目录跟随所选账号刷新，未公开思考能力时只使用服务默认。实现依据 [官方模型与推理](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference) 和 [预览限制](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations)。旧 `codex` 会话保留为可读历史；Host 拒绝在原会话执行，Desktop 继续时新建 `chatgpt` 会话，避免回放不完整的外部状态。

DeepSeek 的旧配置保持 non-thinking，可在设置中启用思考强度。DeepSeek reasoning 与 Claude 完整签名内容块在有效流结束后保存，绑定模型/端点，不显示为回答正文。Claude 支持 adaptive thinking 和适用旧模型的显式 budget；具体档位受模型支持范围约束。图片使用两家官方 SDK 的原生结构，通用兼容接口需显式启用能力。未接 Gemini 原生 API、私有 OAuth、服务端工具或远程 Files API；Claude 自动 prompt caching 尚未启用，只报告服务返回的缓存用量。

模型层移植了 Pi 的 reasoning 回填与 Responses 原生输出项回放机制，保留 item ID、加密 reasoning、phase 和 namespace。来源与 MIT 许可见 [DEPENDENCIES](../src/Workbench.Core/DEPENDENCIES.md) 及 [Responses provenance](../src/Workbench.Core/Licenses/Responses/PROVENANCE.md)；Claude SDK 版本、许可证和边界见 [ANTHROPIC](../src/Workbench.Core/ANTHROPIC.md)。这些是明确范围的模块/行为改编，没有移植 Pi 的定价、完整模型目录或所有 transport。

## 工具边界

- 常规文件工具限制在选定工作区；拒绝路径穿越、符号链接/连接点和 `.git` 内部文件。仅处理 UTF-8 文本；agent 分页读取上限 16 MiB，编辑、符号分析与 GUI 原有整文件预览上限仍为 1 MiB。分页可跨超长行，返回整文件哈希及续页游标；再次请求应携带预期哈希。
- 修改已有文件必须使用读到的 SHA-256；执行前再次核对，避免常见的陈旧内容覆盖。局部替换要求唯一匹配，保留 UTF-8 BOM。它不是与外部编辑器共享的原子事务。
- 读、搜索和 diff 不需要修改审批；写入和命令默认逐次审批。命令在用户权限下运行，能够访问工作区以外资源，当前没有 OS sandbox。常见 provider 密钥从工具子进程的环境移除，但这不等于完整隔离。
- Git 预览禁用 external diff、textconv 和 fsmonitor；未跟踪文件显示名称，需打开内容审阅。
- `list_files`、`search_files`、`glob_files` 支持路径前缀和分页。搜索可选正则、大小写、glob 与上下文；使用 BCL Regex（每行 100 ms 超时）和 Microsoft.Extensions.FileSystemGlobbing。搜索/路径续页有内容/清单快照校验；每次最多扫描 2,000 个文件 / 32 MiB，超限明确标注。Git CLI 提供受忽略规则约束的文件清单，仓库损坏不降级扫描忽略内容；非 Git 目录使用有数量上限的遍历。
- `apply_patch` 改编 OpenCode 补丁解析，支持一次添加、更新、删除、移动多个文件；现有文件要求 SHA-256，所有来源与目标路径都加载项目规则。先检查全部变更，再分阶段落盘，受控失败尝试回滚；多文件操作不保证断电/系统崩溃时的原子性。精确唯一上下文匹配，无模糊猜测；单文件 1 MiB，事务 8 MiB。
- `inspect_symbols` 复用 Microsoft.CodeAnalysis.CSharp 5.9.0，提取 C# 声明、签名、行号和文件哈希。仅做语法分析，不加载项目、不运行构建或分析器，也不声称提供跨文件语义引用或编译验证。
- `inspect_evidence` 是会话绑定的只读工具，默认返回有界来源记录，可按事件序号查看原始工具摘录。写入、命令、下一轮任务使旧证据保守失效；空闲查询也不声称当前文件仍符合历史结果。回读不会重放工具，模型声明和召回结果本身不成为新证据。`session.evidence` 暴露同一投影；目前底层事件读取尚未分页。
- 模型命令使用非交互 shell、显式关闭 stdin，提供超时、取消和进程树终止。`start_command` / `read_command` / `stop_command` 提供后台启动、UTF-8 字节游标日志与停止，日志上限 4 MiB；每工作区最多 4 个活动命令。命令属于本次 run，任务结束/取消或 Host 断开时停止，重启只读历史日志，不重新挂接 PID 或重放命令。刻意脱离进程树的后代不在 BCL 进程树清理保证内；不是 OS 沙箱。
- 多文件补丁的每个产物/删除路径进入证据与经验记录；召回检查完整产物集合和源事件，文件重新出现或哈希改变即失效。后台命令的运行态不算验证成功，只有身份一致的完整终态可作为带来源的观察。
- 所有模型均在本内核的模型/工具边界消费追加指令；审批期间追加的指令会跳过尚未执行的旧动作。停止不会自动撤销已经发生的修改。只读/工作区编辑/完全访问均为工具与审批策略，ChatGPT 也使用同一策略，没有官方或自建 OS sandbox。
- 重启会标记未结束会话为 interrupted，结果未知的工具调用必须核对现场后再继续。崩溃前已确认接收、尚未消费的补充指令保存在 `input.queued` 事件里，不自动执行。
- 交互终端使用 Iciclecreek.Avalonia.Terminal + Porta.Pty，由用户显式打开。它与模型的非交互命令分开；关闭窗口终止其 shell。依赖来源见 [桌面组件记录](../src/Workbench.Desktop/DEPENDENCIES.md)。

## 数据与开发

默认数据目录为 `Environment.SpecialFolder.LocalApplicationData/Workbench`；Windows 为 `%LOCALAPPDATA%\Workbench`。包含 `sessions.db`、非秘密 GUI 设置、实例锁和 `oauth/` 保护存储。Windows 凭据经 DPAPI 加密，macOS 使用 Keychain，Linux 使用 Secret Service/libsecret；后两平台仍待实机验收。旧版可能留下的 `codex/` 目录不读取、不删除。会话数据库含用户任务、代码片段、工具输出、附件及模型原生 continuation，不要上传到公共仓库。API 密钥值与 OAuth token 不保存到 GUI 设置或会话 continuation；其中的账号绑定只是不含密钥的标识。

`--data-dir` 可指定隔离目录。GUI 支持 `--workspace`；`--smoke-test` 启动后自动退出，默认使用临时隔离数据；`--screenshot` 输出桌面渲染。项目构建产物、SDK、模型验收数据均被 `.gitignore` 排除。

开发与发布使用 `scripts/` 下 PowerShell 7.2+ 脚本；依赖精确版本和 NuGet lock files 已入库。发布为 Host + Desktop 并列的 self-contained 目录，不是安装器。没有签名、自动更新、macOS 公证或商店分发。脚本保留原始依赖锁，额外记录 RID 解析结果和 SHA256 清单。

## 本次工具升级验收

本轮结果集中记录在 [工具升级验收](tools-upgrade-validation.md)：实际文件和命令、Unicode 分页、跨目录规则变化、失败回滚、Host 强制退出与两级子进程回收、日志冷启动及经验恢复。全部测试后台运行，未打开 GUI、登录账号或调用付费模型。上一篇 [统一内核验收](unified-kernel-validation.md) 保留对应历史版本的完整证据。

## 历史验收证据（统一内核改造之前）

以下测试数量、Codex 传输检查与发布包仅描述其对应历史版本。`Workbench.Codex` 及其专属传输测试已从当前实现移除；旧报告保留，不计入当前版本的通过数量。

1. 单元/契约测试：Windows 当前 **239 通过、1 项需要显式启用的本机 Codex 测试默认跳过**；严格构建零警告、零错误。报告 `artifacts/test-results/continuity-final.trx`。包括自动摘要与恢复、层级规则、项目经验、实际 SDK 流与序列化、签名/推理状态、图片预算、SQLite 重启、证据召回与失效；UI 测试直接引用真实 Desktop 程序集。原有取消不重放、编辑冲突、进程退出、存储顺序与 Codex 传输测试继续通过。Unix 条件测试不计为 Unix 实机验证；下列模型与实机记录保留为历史证据。
2. `scripts/verify-host.py`：真实 Host + 本地 SSE，执行读取、编辑审批、命令审批、测试、Git diff、证据召回，并比对重启前后的会话和证据投影。本轮通过，5 次工具、2 次审批，报告 `artifacts/protocol-/6cb1b8a6108344e3879490101dc3c8c0/result.json`。
3. `scripts/verify-lifecycle.py` 本轮再次通过：工作区排他、审批期间追加指令、停止、过期审批、崩溃恢复、手工命令独占与 16 个工作请求占满时停止请求仍及时响应。7 项检查的报告位于 `artifacts/lifecycle/2a459955a4984afaa49a0ede09ec0d15/result.json`；没有云端费用。
4. `scripts/verify-host.py --live-deepseek`：2026-10-30 已通过；模型为 `deepseek-flash`，3 次审批、8 次工具调用，三个加法检查通过，原测试与用户笔记哈希不变，重启恢复一致。报告位于本机 `artifacts/live-/b236e2657742411bb0e3553fd1cb2de8/result.json`。
5. Windows GUI：实际连接 Host，创建/恢复会话、文件筛选、C# 高亮预览、Git diff 均已交互验证；终端显示真实 PowerShell 提示符，关闭后 shell PID 退出。新版布局已交互核验文件修改审批、允许后落盘、独立命令审批详情、等待审批时停止、实际变更面板和新会话状态复位；Ctrl+Enter 的事件路由问题已修复并复验。两次离线 GUI 任务主动停止，不记作完整任务成功；命令执行闭环由第 2、4 项验证。范围记录见 `artifacts/ui-validation.json`。保存的真实 DeepSeek 任务渲染截图位于 `artifacts/cogito-completed-task.png`。最小窗口、长 Markdown 压力及延迟 RPC 下连续输入仍待专门交互验收。
6. 上一轮独立包位于 `artifacts/publish/cogito-preview/{win-x64,linux-x64,osx-arm64}`。三包分别核验 303/299/299 个文件哈希、每包 6 份生产源依赖锁和 native headers，中文卫星资源与 Roslyn 均包含，无测试专用 Headless 依赖。Windows 独立 Host 的 Unicode 会话/命令及正常退出通过，报告 `artifacts/release-checks/cogito-preview.json`。清除开发 SDK/Host 环境覆盖和模型密钥后，Windows GUI 分别以中文、英文启动并恢复已保存的真实 DeepSeek 任务，两个 smoke 均退出码 0、stderr 为空；报告与截图位于 `artifacts/preview-localization/`，本轮没有模型调用。Linux/macOS 尚未实机运行；CI 配置不是运行通过的证据。终端复杂应用、中文输入法和完整子进程清理矩阵仍待逐平台验收。

一个很小的真实修复案例验证了链路，不能替代长任务、失败恢复、资源消耗或竞品对比评测。

此前模型层三端包在 `artifacts/publish/model-core-preview/`，310/306/306 个文件及依赖、许可核验通过；Windows 包内 Host 实测通过，未启动 GUI。报告 `artifacts/release-checks/model-core-preview.json`。

前轮模型测试、真实调用失败与后续隔离结果保留在[模型与证据召回验收](model-core-validation.md)；摘要、层级规则、本地 skills 与项目经验的上一阶段验收见[连续性验收](continuity-validation.md)。其中 API 与官方 Codex 分开执行的描述是当时架构，已由本次统一内核取代。完整自学习与远程技能安装仍不在当前实现范围。

本轮 Windows 多语言交互另见 `artifacts/ui-localization-validation.json`：同一设置窗口中完成中文→英文→中文往返，验证选中模型入口文案、标签宽度重排、减少动画偏好持久化和文件原文预览。修复了实际窗口中发现的静态 XAML 文案、ComboBox 选中显示和 TabControl 动态宽度问题。此轮只验证界面，没有提交 agent 任务或调用云端模型；不能把 GUI fixture 的任务未执行当作完整编码任务成功。

## 接下来按顺序推进

1. 可靠编码：在已落地的自动摘要、层级规则、经验工具与分页检索之上，推进会话分支、checkpoint 审阅、结构化 diff 和固定任务评测。
2. 建立至少 20 个固定仓库任务及故障案例的基线，记录成功率、人工介入、耗时、token 和实际 API 成本；再决定 MAF 或自有循环的长期边界。
3. 三端实机矩阵按用户要求暂缓，待设备具备后检查中文输入法、缩放、终端、进程清理、文件权限、登录回调和发布包；签名/公证单独处理。跨平台构建继续验证。
4. 高频 work：受控网络检索、Playwright 浏览器验证、文件与简单文档产物。通过 MCP C# SDK 扩展，不把全部办公生态塞进首版。
5. 研究增强：仓库记忆/代码定位、GEPA 离线 skill 优化、预算内只读并发。每项与基线独立对照；目前没有训练任何论文模型，也未复现论文收益。

研究细节与来源见 [开源](../research/opensource.md)、[平台](../research/platform.md)、[论文](../research/papers.md)、[总路线](../research/architecture-roadmap.md)。
