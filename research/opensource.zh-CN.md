[English](opensource.md) | **简体中文**

# 开源 agent 借鉴清单：面向 C#/.NET 编码优先桌面应用

检索日期：2026-10-30（Asia/Shanghai）。最终定位是 Windows、Linux、macOS 的编码优先 agent：先把代码理解、修改、执行、验证、审阅做完整，再补充检索、文件/表格/文档处理和浏览器等高频 work 能力。本轮为研究与技术路线，不包含实现或性能实测。

## 结论

主线组合是：**Pi 的小内核与上下文管理，Codex 的客户端协议，OpenCode 的输入队列与恢复，Aider 的代码结构检索，mini-swe-agent 的可测基线**。吸收 Gemini CLI/Cline 的变更审阅和 checkpoint，并从 OpenHands 借鉴 workspace/backend 分层。Goose 只为少量高频 work 模板与 MCP 扩展提供参考；browser-use 服务于网页研究和 Web 应用测试。

完整桌面接管、多应用编排、常驻消息 Gateway 和跨设备助手后置。下文保留这些项目的调研证据，便于以后扩展；它们不构成首版交付范围。

优先闭环是：理解仓库 → 定位相关代码 → 编辑补丁 → 运行构建/测试 → 修复失败 → 展示 diff 与验证证据；整个过程可以纠偏、取消和恢复。多 agent、长期记忆、自进化应通过相同任务预算下的实验后再加入默认路径。

下面的 C# 类型和模块名都是本项目的设计建议，不是上游已有 API，也不声称 .NET 已自带这些 agent 能力。

## 项目矩阵

“源码抽查”表示阅读了对应核心文件的相关实现；“文档核验”表示阅读一手 README/文档和目录结构，未完整审计实现。许可证栏是检索时仓库根目录标识，模型权重、数据、第三方资源和独立服务还需按各自许可证处理。

| 项目与当前仓库 | 核验深度 / 根许可证 | 最值得借鉴 | 在本项目的位置 |
|---|---|---|---|
| [Pi](https://github.com/earendil-works/pi) | loop 源码抽查；session/RPC/compaction 文档；[MIT](https://github.com/earendil-works/pi/blob/main/LICENSE) | 可组合小内核、steering/follow-up 分离、树状会话、工具事件、可扩展压缩 | 核心循环与上下文层；优先级最高 |
| [Codex](https://github.com/openai/codex) | 官方协议文档、协议源码目录；[Apache-2.0](https://github.com/openai/codex/blob/main/LICENSE) | thread/turn/item、双向请求、审批和中断、客户端与运行时分离 | Host 协议与生命周期；优先级最高 |
| [OpenCode](https://github.com/anomalyco/opencode) | inbox/execution 源码抽查；[MIT](https://github.com/anomalyco/opencode/blob/dev/LICENSE) | 输入队列、steer 提升、会话互斥、执行所有权和重启连续性 | 会话状态机与运行恢复；首版主线 |
| [Aider](https://github.com/Aider-AI/aider) | repomap.py 源码抽查；[Apache-2.0](https://github.com/Aider-AI/aider/blob/main/LICENSE.txt) | 符号/引用图与 PageRank，在 token 预算内选择代码上下文 | 仓库理解与上下文选择；首版主线 |
| [mini-swe-agent](https://github.com/SWE-agent/mini-swe-agent) | default agent 源码抽查；[MIT](https://github.com/SWE-agent/mini-swe-agent/blob/main/LICENSE.md) | 极简 model/environment/loop 边界、轨迹序列化、步数与成本限制 | 从第一阶段建立编码评测基线 |
| [OpenHands](https://github.com/OpenHands/OpenHands) | 当前 Canvas/SDK README、目录与边界文档；[MIT](https://github.com/OpenHands/OpenHands/blob/main/LICENSE) | Canvas、Agent Server、SDK、调度服务分工，跨 backend 的 agent 运行 | 先抽象 workspace/backend；容器和远端随后实现 |
| [Gemini CLI](https://github.com/google-gemini/gemini-cli) | checkpoint 文档、scheduler 目录；[Apache-2.0](https://github.com/google-gemini/gemini-cli/blob/main/LICENSE) | 文件改写前 checkpoint、影子 Git + 会话 + 原工具调用的联合恢复 | 文件工具的撤销体验 |
| [Cline](https://github.com/cline/cline) | README；[Apache-2.0](https://github.com/cline/cline/blob/main/LICENSE) | 可审阅 diff、checkpoints、Plan/Act、工具审批 | 结果审阅与任务模式 UI |
| [Open Interpreter](https://github.com/openinterpreter/openinterpreter) | 当前 README 与目录；[Apache-2.0](https://github.com/openinterpreter/openinterpreter/blob/main/LICENSE) | 模型特定 harness、Codex/ACP 兼容、外置 QA/computer 工具 | 可插拔模型策略与第三方运行时适配参考 |
| [Goose](https://github.com/aaif-goose/goose) | README、recipe schema、extension manager 目录；[Apache-2.0](https://github.com/aaif-goose/goose/blob/main/LICENSE) | 参数化 recipes、MCP 扩展生命周期 | 少量高频 work 模板与工具生态；编码闭环后补充 |
| [browser-use](https://github.com/browser-use/browser-use) | agent service 源码抽查；[MIT](https://github.com/browser-use/browser-use/blob/main/LICENSE) | 状态观察、页面与动作重复检测、暂停/停止检查、截图轨迹 | 网页研究和 Web 应用测试；限定场景扩展 |
| [UI-TARS Desktop / Agent TARS](https://github.com/bytedance/UI-TARS-desktop) | README、BrowserOperator 源码抽查、operator 目录；[Apache-2.0](https://github.com/bytedance/UI-TARS-desktop/blob/main/LICENSE) | 观察与动作的 Operator 抽象、本地/远端执行、DOM/视觉混合浏览器、事件回放 | 后置桌面控制与视觉操作参考 |
| [OpenClaw](https://github.com/openclaw/openclaw) | README、gateway architecture/protocol 文档；[MIT](https://github.com/openclaw/openclaw/blob/main/LICENSE) | 常驻 Gateway、设备身份/能力、幂等请求、多界面接入 | 后置常驻服务、远程设备和消息渠道 |
| [Agent S](https://github.com/simular-ai/Agent-S) | S3/S4 README、实现目录；[Apache-2.0](https://github.com/simular-ai/Agent-S/blob/main/LICENSE) | 任务决策与 grounding 分工，行为候选选择，离线演化工具接口 | 研究插件与离线实验 |
| [OpenCUA](https://github.com/xlang-ai/OpenCUA) | README、data README、评测目录；[MIT](https://github.com/xlang-ai/OpenCUA/blob/main/LICENSE) | 跨平台示范记录、截图/输入/可访问性树同步、状态-动作对齐 | 轨迹数据和评测基础设施 |

## 需要更新的旧印象

- `badlogic/pi-mono` 目前重定向到 `earendil-works/pi`；代码和文档中的包名称也有演变。不能只按旧博客硬编码包名。
- `block/goose` 目前重定向到 `aaif-goose/goose`。
- OpenCode 当前核心在 `packages/core/src/`；常见文章里的 `packages/opencode/src/` 路径不再代表这次检索的主干布局。
- OpenHands 当前根仓库是 Agent Canvas；Python 内核和 Agent Server 在 [`OpenHands/software-agent-sdk`](https://github.com/OpenHands/software-agent-sdk)，调度/触发服务有独立仓库。不要套用早期单仓 `openhands/controller` 架构。
- Open Interpreter 当前主干明确是基于 Codex 的 Rust 版本。原 Python 项目由 README 指向社区分支 [`endolith/open-interpreter`](https://github.com/endolith/open-interpreter)。本报告没有把早期 Python 实现当作当前主干。
- OpenCUA 是 **NeurIPS 2025 Spotlight**；Agent S3 当前仓库标注 TMLR 2026，S4 是 2026 arXiv 工作。这些不能未经会议页面核验就列入“2026 顶会 oral/spotlight”。

依据：[Pi 当前仓库](https://github.com/earendil-works/pi)、[Goose 当前仓库](https://github.com/aaif-goose/goose)、[OpenHands README](https://github.com/OpenHands/OpenHands/blob/main/README.md)、[Open Interpreter README](https://github.com/openinterpreter/openinterpreter/blob/main/README.md)、[Agent S README](https://github.com/simular-ai/Agent-S/blob/main/README.md)、[OpenCUA README](https://github.com/xlang-ai/OpenCUA/blob/main/README.md)。

## 一、从 Pi 抄内核边界和会话语义

`packages/agent/src/agent-loop.ts` 把领域消息转换与模型调用分开：先 `transformContext`，再 `convertToLlm`。循环区分运行中的 steering 和当前工作结束后的 follow-up，并支持取消、工具开始/更新/结束事件，以及 `beforeToolCall`/`afterToolCall`。当前实现按配置和工具的 executionMode 选择顺序或并行，不能把所有工具都默认为可并行。[源码](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/agent/src/agent-loop.ts)

对 C# 的映射建议：

- `AgentRuntime` 只负责一次运行的状态推进；模型适配器、上下文策略、工具注册表和事件存储单独注入。
- 用 `CancellationToken` 贯穿模型请求、工具和子进程；`Channel<AgentInput>` 接收运行中用户输入，领域层明确 `Steer` 与 `FollowUp`。
- 工具声明资源范围和并发方式。同一桌面、同一浏览器 tab、同一输出文件的写操作串行；互不依赖的读取可并行。
- 用 `IAsyncEnumerable<AgentEvent>` 向 Host 输出事件，UI 仅订阅和投影。

Pi session 是 JSONL，并通过 `id/parentId` 形成树；分支不会简单覆盖旧历史。压缩保留最近上下文，记录压缩边界与 summary，并避免把工具结果切离工具调用。分支摘要与压缩可以由扩展接管。[Session 格式](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/coding-agent/docs/session-format.md)、[Compaction](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/coding-agent/docs/compaction.md)

可借鉴为“原始事件不可变 + 当前分支/摘要是投影”，持久化可用 SQLite；不要让摘要成为唯一事实记录。编码任务摘要应保留目标、约束、已修改文件、测试结果、未解决失败和下一步；文件或浏览器扩展再增加相应应用状态与产物引用。

Pi RPC 还给出一个很实用的边界：`prompt` 成功只是已接受输入；低层 `agent_end` 后还可能压缩、重试或继续 follow-up，完整静止状态是 `agent_settled`。流应持续读取并处理 backpressure，stdout 用协议，stderr 用诊断。[RPC 文档](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/coding-agent/docs/rpc.md)

GUI 应分别显示“请求已接受”“运行中”“等待用户”“正在恢复”“已完成”，不要把一次 HTTP/RPC 成功回包解释成任务完成。

## 二、从 Codex 抄 Host 与 GUI 的协议

Codex App Server 给富客户端暴露 thread、turn、item 和流式事件。它使用双向 JSON-RPC 风格消息，stdio 传输采用 JSONL；服务端可以发起需要用户回答的请求。CLI 可以导出与自身版本对应的 TypeScript/JSON Schema，适合生成客户端类型。当前官方文档把 WebSocket 标为 experimental，因此本地首版适合优先做受控子进程通信。[官方 App Server 文档](https://learn.chatgpt.com/docs/app-server)

本项目可以采用相同分层思想：

1. **Contracts**：版本化命令、事件、工具输入输出、能力声明。
2. **Host**：会话生命周期、执行调度、工具和模型连接、持久化。
3. **Desktop**：时间线、工具卡片、审批卡片、文件/浏览器预览、设置。

审批要关联具体动作 ID 和当时参数。取消和排队都要有明确应答；重连后可恢复待处理交互。UI 崩溃不应导致已执行的外部动作被自动重放。生成代码、保存文件、提交网页表单的“撤销”语义不同，协议里应表达实际可逆性。

建议读源码入口：[`app-server-protocol/src/protocol/v2`](https://github.com/openai/codex/tree/02ca1e0d990e496366bda7c30e2981cf296783ce/codex-rs/app-server-protocol/src/protocol/v2)、[`app-server`](https://github.com/openai/codex/tree/02ca1e0d990e496366bda7c30e2981cf296783ce/codex-rs/app-server)。本轮重点核验了对外协议，没有声称完成 Codex 全内核审计。

## 三、从 OpenCode/OpenHands 抄队列、工作区与运行恢复

OpenCode 当前 `SessionInbox` 在每个 session 上串行化操作；pending input 可提升为 steer，运行中的 step boundary 与空闲输入有不同消费规则。`SessionExecution` 区分用户中断与 shutdown，中断接受和清理完成分开；源码还处理 shutdown 保留运行 claim 的重启连续性。[Inbox](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/session/inbox.ts)、[Execution](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/session/execution.ts)

这适合映射成 C# 的每会话串行处理器：输入先持久化，状态转移写事务，再发事件；有明确执行所有者。恢复时先 reconcile 已记录动作，而不是从最后一条用户消息盲目重跑。

OpenHands 当前把 Canvas、Agent Server/SDK 和 automation 分开，Canvas 可以连多个 server，也支持第三方 ACP agent。值得借鉴 `Workspace` 和 `ExecutionBackend` 的独立边界，让本机、容器、远端机器作为可替换执行环境。[Canvas README](https://github.com/OpenHands/OpenHands/blob/main/README.md)、[SDK README](https://github.com/OpenHands/software-agent-sdk/blob/main/README.md)

ACP 可以作为接入第三方完整 agent 的适配协议；MCP 连接工具。两者的生命周期和语义不同，本项目内部协议要覆盖自己的 GUI 状态，再通过 adapter 对接。

## 四、编码主能力：Aider、mini-swe-agent、Gemini CLI、Cline

这些机制直接决定产品主能力，应在首版路线中实现并评测。基础工具包括代码搜索、读写/补丁、进程/终端、Git 状态/diff 和构建测试；上下文结构索引、checkpoint 与审阅体验在这条主线上迭代。

**Aider**：`repomap.py` 用 tree-sitter 提取信息，构造图、计算 PageRank，并按 token 预算输出相关代码片段。可借鉴“结构检索 + 预算管理”；C# 工程可使用 Roslyn，其他语言通过 LSP/tree-sitter 适配，不能宣称 Roslyn 覆盖任意语言。[源码](https://github.com/Aider-AI/aider/blob/main/aider/repomap.py)

**mini-swe-agent**：`DefaultAgent` 把 model 与 environment 注入，显式检查 step/cost limits，保存可重放分析的轨迹。源码标明 cost limit 检查可在一次调用计费后超出阈值，因此若要实现严格总预算，还需要本项目做调用前预留与并发配额。[源码](https://github.com/SWE-agent/mini-swe-agent/blob/main/src/minisweagent/agents/default.py)

**Gemini CLI**：文件改写 checkpoint 包含 shadow Git snapshot、对话历史和待执行工具调用，并不修改用户项目的 Git 历史。值得借鉴联合恢复概念，但不能把 Git snapshot 当成对邮件、网页提交、数据库更新的通用回滚机制。[Checkpointing](https://github.com/google-gemini/gemini-cli/blob/main/docs/cli/checkpointing.md)

**Cline**：Plan/Act、可审阅 diff 和 checkpoint 让用户理解即将发生及已经发生的变更。可用于桌面 agent 的“产物预览/操作预览”，审批粒度仍要按具体工作流设计。[README](https://github.com/cline/cline/blob/main/README.md)

## 五、从 Goose 吸收少量高频 work 模板

Goose 当前提供 desktop、CLI、API 和 MCP 扩展。最适合通用桌面产品借鉴的是 recipe：把任务说明、参数、扩展需求、结构化结果和重试策略打包。它的参数可以映射成 GUI 表单，activities 可以变成可点击任务入口。[README](https://github.com/aaif-goose/goose/blob/main/README.md)、[Recipe schema](https://github.com/aaif-goose/goose/blob/main/documentation/docs/guides/recipes/recipe-reference.md)

在编码闭环成熟后，可选“从网页形成研究简报”“生成项目报告”“核对两份表格”等少量高频任务作为扩展，每项明确输入、输出和验收条件。优先沿用已有文件、进程、检索和浏览器工具；暂不发展完整办公套件或多应用自动化平台。

C# 侧建议设计 `WorkflowDefinition`、`WorkflowParameter`、`RequiredCapability`、`ArtifactExpectation`。工具连接器采用 MCP；业务流程仍由自己的运行时管理，避免把会话控制和工具协议揉在一起。

源码入口：[`recipe/manifest.rs`](https://github.com/aaif-goose/goose/blob/main/crates/goose/src/recipe/manifest.rs)、[`recipe/validate_recipe.rs`](https://github.com/aaif-goose/goose/blob/main/crates/goose/src/recipe/validate_recipe.rs)、[`extension_manager`](https://github.com/aaif-goose/goose/tree/main/crates/goose/src/agents/extension_manager)。本轮验证了 schema 与入口，未实测各扩展。

## 六、浏览器能力与后置桌面控制参考

本节分两层采用：浏览器检索、资料提取和 Web 应用测试进入高频扩展；完整桌面控制、跨应用编排和视觉定位作为后续研究，不放入首版主路径。

UI-TARS/Agent TARS 把模型与 Operator 分开，目录中可见 browser、nutjs、ADB、AIO 等实现。`BrowserOperator` 明确提供 screenshot、supportedActions、execute，动作执行逐项进行；截图带尺寸信息，支持显示动作提示。Agent TARS 文档还描述 DOM、视觉及混合浏览器策略。[README](https://github.com/bytedance/UI-TARS-desktop/blob/main/README.md)、[BrowserOperator 源码](https://github.com/bytedance/UI-TARS-desktop/blob/3ac2cb8d946d78d5ff92ad17b318eaafa18c178c/multimodal/gui-agent/operator-browser/src/browser-operator.ts)

浏览器扩展可先设 `IBrowserOperator`，每次动作绑定 `ObservationId` 与目标 tab，返回结构化结果和下一次观察。未来实施 `IComputerOperator` 时再处理窗口、坐标系和屏幕缩放；Windows UI Automation、macOS Accessibility、Linux AT-SPI/显示服务器适配需要各平台实现。这些桌面后端不属于本轮建议的首版工作。

`browser-use/agent/service.py` 的循环含 context preparation、next action、execute actions、finalize；在模型调用后及动作前检查暂停/停止。它记录页面状态和动作重复，用 stagnation/repetition 向模型提示循环问题，并保存每步截图轨迹。[源码](https://github.com/browser-use/browser-use/blob/2cc71890e65056b67fdb29d98bb407749c15bb08/browser_use/agent/service.py)

值得移植的能力：

- 优先用结构化 API/应用接口，其次 DOM 或可访问性树，视觉坐标作为补充；不同途径共享一套结果和证据结构。
- 执行后重新观察。按钮点击成功与任务业务成功分别记录。
- 发现页面/动作重复时退出当前策略，刷新观察、换定位方式或请求用户补充，而不是无限重试。
- 为截图、下载文件、页面状态保存 artifact 引用。UI 能从工具卡片跳到动作前后证据。

上述动作后验收与多通道策略是基于这些项目提出的本项目设计，尚未验证成功率收益。UI-TARS Desktop README 的本地平台声明是 Windows/macOS/Browser；本轮不能把它扩张为已验证完整 Linux 桌面支持。

## 七、后置参考：OpenClaw 常驻服务与设备能力

OpenClaw 的 Gateway 管理会话、工具、事件与渠道连接；客户端和 nodes 使用带身份及 capability 的 WebSocket 握手。请求、应答和事件分离，带副作用的方法使用幂等键。架构文档把设备身份、配对、连接生命周期和定时工作写成独立概念。[Architecture](https://github.com/openclaw/openclaw/blob/76985e4c31c4c94c91e7ed79411c8189180b7ea8/docs/concepts/architecture.md)

可提前吸收 request ID、幂等和运行记录的思想。未来确有长期任务或多设备需求时，再引入 GUI 退出后仍运行的常驻服务、设备能力协商和配对。

首版做本地桌面与 CLI，共享 Host；远程 node、消息渠道、个人助手 Gateway 不进入默认路线，待真实用户需求验证后单独立项。

## 八、后置研究：Agent S/OpenCUA 的桌面数据与策略

Agent S3 把通用任务模型与 grounding 模型组合，README 明确其单显示器约束；行为 Best-of-N 的结果和单次运行分开报告。它的基准数值不可直接外推为普通用户桌面任务的可靠率。[S3 README](https://github.com/simular-ai/Agent-S/blob/main/gui_agents/s3/README.md)

Agent S4 让 Builder 改写一个供任务 agent 使用的 Python 工具接口，并通过独立 benchmark 运行选择接口变体。这提示未来可离线学习高频流程的专用工具，但不宜让生产 agent 自由改写自己的权限和执行核心。[S4 README](https://github.com/simular-ai/Agent-S/blob/main/agent_s4/README.md)

OpenCUA 的 AgentNet 工具同步记录屏幕、鼠标/键盘事件和可访问性树；数据处理把动作与开始前最后一个视觉变化帧对齐，减少未来信息泄漏。可用于设计自有跨平台轨迹格式、人工示范和回放评测。[数据说明](https://github.com/xlang-ai/OpenCUA/blob/main/data/README.md)

首版的编码轨迹先记录 `Context → ToolCall → Result → Verification`，以及仓库版本、补丁、测试结果、模型、token/费用与失败原因。未来增加视觉桌面任务时，再扩展为本节的截图/动作轨迹。记忆检索、候选搜索、自进化的收益通过冻结任务集测量后决定。

## 推荐吸收顺序

下表按机制吸收批次分组；正式产品阶段及发布条件以 [产品架构与实施路线](architecture-roadmap.md) 为准。

| 吸收批次 | 要吸收的机制 | 可验证的完成条件 |
|---|---|---|
| A 编码内核 | Pi loop + Codex lifecycle + OpenCode inbox + mini-swe 基线 | 使用同一个 mock provider 验证取消、工具错误、插队与恢复；以真实 provider 完成一条读代码→改代码→测试闭环 |
| A 桌面工作台 | 跨平台 GUI、代码/工具时间线、终端输出、diff 审阅 | Win/Linux/macOS 分别实测打开仓库、启动子进程、取消和保存会话；GUI 不判定执行状态 |
| B 编码质量 | Aider 检索、checkpoint、OpenHands workspace 边界 | 在冻结编码任务集、固定模型与成本下相对简单基线提高成功率；测试结果与补丁可追踪 |
| B 可靠运行 | 会话互斥、执行所有权、崩溃恢复、预算记录 | Host 重启/客户端断线不会盲目重复已完成操作；待处理交互可恢复 |
| C 高频 work | Goose 式少量模板 + MCP；检索、文档/表格、文件处理 | 复用主内核，每项扩展只有清楚的输入、产物和验收；避免独立办公套件与多应用编排 |
| C 浏览器 | browser-use 状态循环；网页研究、Web 应用测试 | 处理 DOM 变化、页面重复、下载和证据；用户可中止，产物可验证 |
| 条件扩展 | 容器/远端 backend、受控多 agent、记忆或候选选择 | 先有真实需求或冻结评测收益，再增加默认复杂度 |
| 后置探索 | UI-TARS/Agent S/OpenCUA 桌面接管；OpenClaw Gateway | 单独验证市场需求与平台适配成本，不阻塞编码产品交付 |

## 复用代码的实际边界

本报告确认的是各仓库根许可证和读取的来源，未进行完整依赖许可证审计。实际搬代码时记录原始仓库、commit、源路径及改动；保留对应 LICENSE/NOTICE/版权声明。模型权重、数据、图标、商标和托管 API 不能只按仓库代码许可证推断。对本项目而言，优先复用协议、成熟工具客户端和公共库；把 TS/Rust/Python 的整套依赖链翻译进 C# 通常并不省代码。

## 本次核验范围和限制

使用了官方仓库、raw 源文件、GitHub API 的默认分支/根许可证/tree，以及 Codex 官方协议文档。重点源码抽查是 Pi agent loop、OpenCode inbox/execution、UI-TARS BrowserOperator、browser-use agent service、Aider repomap、mini-swe-agent default agent；其余按上表标注为文档和目录核验。没有运行这些产品、没有实测其跨平台兼容性或比较成功率、没有将 star 数作为质量证据，也没有声称覆盖市场上所有 agent。

可复现的部分主干快照：Pi `e7a0df27c09d11bf4161d3605246cbf57a129cb2`；Codex `02ca1e0d990e496366bda7c30e2981cf296783ce`；OpenCode `60072747957926e7aeec7e911fe6e23abe9bf830`；UI-TARS Desktop `3ac2cb8d946d78d5ff92ad17b318eaafa18c178c`；browser-use `2cc71890e65056b67fdb29d98bb407749c15bb08`；OpenClaw `76985e4c31c4c94c91e7ed79411c8189180b7ea8`；Agent S `0fff0e634869dab72027435b4586e62300185d41`；OpenCUA `dfc91ba89f700d10f26ec50362d308571482ab8b`。文中标注 main/dev 的链接会随上游更新，实施前应重新固定版本。
