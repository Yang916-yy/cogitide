[English](model-core-validation.md) | **简体中文**

# 模型层与证据召回验收

日期：2026-10-30。此处记录本轮实现和实际结果，区别于研究设计或上一轮发布包。

这是模型层那一轮的历史快照，包含当时的成功和失败记录。后续自动摘要、规则、skills、经验与最新测试见[连续性验收](continuity-validation.md)；下文“尚未实现”描述的是该历史版本。

## 实现

- DeepSeek：通过官方 OpenAI .NET 2.14 SDK，配置 none/low/high/max；回传所有历史 assistant reasoning，包括无工具轮次；按 endpoint/model/protocol 隔离 opaque state；记录缓存和推理用量。
- Claude：官方 Anthropic C# 13.3 SDK 原生 Messages，SDK HTTP/SSE/聚合器，完整有序内容、thinking/signature/redacted thinking、工具错误、图片和用量。取消、超时、损坏/截断流不提交工具或 continuation；不自动重试。
- 图片：本地 PNG/JPEG/GIF/WebP，最多 4 张、单张 4 MiB、每次请求总计 8 MiB。历史选择同时受文本、完整 continuation、图片数量和字节预算约束；按完整轮次移出旧历史，不拆工具配对。只有文件头/MIME 验证，不宣称完整像素解码或维度验证。附件 UI 当前显示文件名，可移除草稿附件，不提供缩略图。
- 设置：原生服务选择、预设、服务对应的思考档位、Claude 旧版预算、通用兼容接口图片 opt-in；中英文即时更新。缓存、推理 token 为原用量的细分，不重复加入总量。
- 证据：`EvidenceMemory` 从持久事件生成有界投影，来源包括事件、参数和结果 SHA-256；明确区分命令成功退出、失败、超时、错误和未知。哈希仅证明记录一致，不证明内容正确。写入/命令及新任务边界使旧结果失效；空闲状态的派生视图全部 Stale。`inspect_evidence` 和 `session.evidence` 提供查询，不修改原始历史。

网络与系统组件没有移植 TS 的基础设施：使用 BCL、两家官方 SDK、现有 SQLite/Avalonia。实际 TS 规则移植范围是 Pi 的空 reasoning 回填，固定 commit 与 MIT 文本见 [依赖来源](../src/Workbench.Core/DEPENDENCIES.md)。未把这个小改编称为完整 provider 模块移植。

## 自动验收

- `dotnet build Workbench.slnx --no-restore -warnaserror`：0 警告、0 错误。
- 全套测试：190 通过、1 跳过、0 失败，`artifacts/test-results/model-core.trx`。跳过项要求显式启用本机 Codex 集成；没有把它计为失败或实机验证。
- 全 solution `restore --locked-mode` 通过；Host/Tests 锁包含新依赖。
- 三端独立包在 `artifacts/publish/model-core-preview/{win-x64,linux-x64,osx-arm64}`，分别 310/306/306 个文件；校验和、架构、6 份生产依赖锁及 SDK/移植许可证通过，7 份源码锁不变。生产包无 Headless。报告 `artifacts/release-checks/model-core-preview.json`；Windows 包内运行时 Host 实测通过，未启动 GUI，Linux/macOS 未实机运行。
- 本地真实 Host：`artifacts/protocol-/6cb1b8a6108344e3879490101dc3c8c0/result.json`。5 次工具、2 次审批、3 个加法断言；用户文件哈希保留；会话、事件和证据投影经进程重启一致。召回返回源记录而没有重复调用修改工具。
- `verify-lifecycle.py` 的工作区所有权、steering、取消、过期审批、崩溃恢复、手工命令独占、16 工作槽占用时停止可用，7 项再次通过。
- 实际 Desktop 程序集的 headless 控件测试覆盖旧配置往返、provider 切换、推理选项、双语和预算检查。新增主界面入口与 Claude 设置进行了 Windows 人工式交互检查；按用户最新要求，后续不再用实际键鼠抢占当前桌面。附件文件选择器的完整交互流程未单独验收。

## 真实 DeepSeek 调用：保留失败

混合编码与图片任务位于 `artifacts/live-/035ea64df26843688b3803cf6a16b060/`，模型 `deepseek-flash`、thinking low。修复加法、运行原有 3 个断言和保留用户文件的检查通过；但模型把**红色** 128×128 PNG 说成白色，所以这次**整体验收失败**，没有改期望答案或把失败计为通过。

后续隔离报告：`artifacts/model-isolation/69c7757e71b34dc38a3dcd3335fb21ea/result.json`。

1. 冷进程加载同一会话，再发仅报告的请求：完成，无工具调用，continuation 再次保存；模型报告原有三个断言的验证范围。
2. 同一 PNG，独立视觉请求，thinking none：回答 `Red`，完成，无工具调用。
3. 同一 PNG，独立视觉请求，thinking low：回答 `Red`，完成，无工具调用；服务返回 13 reasoning tokens。

图片文件已实际查看为红色；真实 SDK 序列化夹具验证 data URI 与输入字节一致。上述结果证明独立视觉和状态续接链路可以工作，不能证明混合长任务视觉稳定，更不能据单一失败断定原因来自模型或应用某一层。完整历史原样保留用于后续排查。

低思考强度下部分有效响应的 reasoning 内容为空，另一些非空；必须保存并回填空字符串，不能用“每轮非空”作为协议正确性的条件。本机未配置 `ANTHROPIC_API_KEY`，Claude 仅通过真实 SDK 的离线协议与持久化测试，尚无真实 Claude 服务验收。

## 当前边界与下一步

当前有根 `AGENTS.md`、完整工具组裁剪、跨重启状态和按源事件召回；尚无模型自动摘要、增量记忆 delta、层级目录规则、产品 skills runtime 或跨任务 procedure 学习。证据投影/响应有界，但底层读取会话全部事件，尚未做到分页 I/O。字符预算不是精确 tokenizer；不声明性能优势或研究收益。

下一步先按 Pi 的成熟压缩语义建立基线，再加入 ACE 启发的增量状态和保守失效，按相同模型、时间、token 预算对照。论文、会议信息和实验分组见 [长任务设计](../research/core-memory-design.md)。
