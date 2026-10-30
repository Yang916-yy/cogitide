[English](continuity-validation.md) | **简体中文**

# 自动摘要、项目复用与输入区验收

日期：2026-10-30。全部测试在后台运行；本轮没有启动原生桌面窗口。内部代号仍为 Cogito。

## 实现范围

| 能力 | 现在的行为 | 复用方式 |
| --- | --- | --- |
| 自动摘要 | 达到字符预算的预留阈值后生成结构化摘要，保留用户原文与最近完整工具轮次；全历史不删除 | Pi 的倒序切点、六节摘要及更新流程，OpenCode 的预留与工具观察裁剪，改写为 C# |
| 连续性 | 完整摘要和会话一起保存；覆盖前缀 SHA-256 校验；中断摘要不提交，恢复不重放工具 | 现有 SQLite 事务、records、System.Text.Json 源生成、异步流与取消令牌 |
| 项目规则 | 根到目标目录的 AGENTS.md；按请求刷新；首次发现新规则、审批期间规则变化，都阻止旧提案直接执行 | OpenCode 层级发现语义 + 现有工作区路径解析器 |
| 本地 skills | 先加载名称/描述，匹配后 read_skill 读取内容；SKILL.md 终止目录递归，支持显式调用限制 | Pi 发现/目录行为 + YamlDotNet 18.1.0，未另写 YAML 解析器 |
| 项目经验 | remember_experience 保存适用条件、步骤和原始工具证据；recall_project_memory 跨同项目会话检索、检查来源与文件变化 | 现有事件库中的小条目，关键词检索、去重、明确替代 |
| 输入区 | 上传、执行模式、模型、思考程度在输入框底部；发送/停止共用按钮，Ctrl+Enter 保留追加指令 | Avalonia 原生控件、现有 ResourceManager 中英文资源 |
| 官方账户入口 | model/list 返回实际思考档位，turn/start 传递 effort、图片和 sandboxPolicy | 本机 Codex CLI 0.164.0 生成的官方 app-server 协议，未自行实现登录或 OS 沙箱 |

以上摘要、规则、skills、项目经验工具属于 API agent 内核；官方 Codex 入口仍由官方 app-server 管理自身执行上下文。API 的只读/工作区编辑/完全访问是工具与审批策略，**不是操作系统沙箱**。只读模式不向模型暴露命令/写文件工具，并拒绝模型强行调用；工作区编辑默认审批，完全访问省略写入审批。

摘要失败时不悄悄丢弃历史用户要求：能保留时继续使用有界原始上下文，不能保留则明确结束。AutoCompact=false 是显式保留的旧裁剪模式。摘要请求不带工具，不传私有推理，中文序列化后的长度也计入请求预算；模型用量单列为摘要消耗。

规则与技能内容不依赖旧摘要，正常请求及摘要完成后重新加载；批准等待结束、执行动作之前再次核对规则。规则是目录作用域的指令，不能扩大执行权限。

## 研究增强的实际边界

经验条目借鉴 [ACE 的增量上下文更新（ICLR 2026）](https://proceedings.iclr.cc/paper_files/paper/2026/hash/8a94ff6f922d995d7d3f4ebf4143e442-Abstract-Conference.html) 与 [Agentic Plan Caching 的条件化计划复用（NeurIPS 2025）](https://proceedings.neurips.cc/paper_files/paper/2025/hash/9549f7d06700f0966d5f938f1d11022a-Abstract-Conference.html)。本轮重新核验了正式 proceedings，未把这两篇标为已核实的 Oral/Spotlight。

实现的是小条目追加、明确前置条件和来源检查；没有完整移植 ACE Generator/Reflector/Curator，也未实施学习检索器、自动全局知识图谱或 GEPA。程序检查能说明来源是否完整、已报告文件哈希是否仍匹配，不能证明自然语言经验普遍正确，所以所有条目始终标记 Candidate。损坏的替代条目不能隐藏原条目，回忆/保存结果也不能循环充当新的原始证据。

固定源码版本和许可证见 [压缩 provenance](../src/Workbench.Core/Licenses/Compaction/PROVENANCE.md)、[规则与技能 provenance](../src/Workbench.Core/PROJECT-REUSE.md)。Pi 固定 `e7a0df27c09d11bf4161d3605246cbf57a129cb2`，OpenCode 固定 `60072747957926e7aeec7e911fe6e23abe9bf830`；原始 MIT notices 随包分发。

## 本轮验证

- 严格全量构建：**0 警告、0 错误**。全量测试：**239 通过、1 跳过**；跳过项是需要显式启用的本机 Codex 握手测试。报告：`artifacts/test-results/continuity-final.trx`。
- 新增回归覆盖：多次摘要、冷启动续接、部分流失败/取消、原文约束、工具完整配对、摘要期间追加指令、摘要来源失效、中文编码预算、规则刷新、经验来源/替代/陈旧文件、真实 Desktop 的输入区与语言状态。
- `scripts/verify-continuity.py` 使用真实 Host、SQLite、文件工具和本地 HTTP 模型：**7 次摘要、32 条完整历史、28 次请求、11 类检查通过**。包括首次子目录写入与规则更新后写入均先阻止，重启不重放、跨会话经验召回、文件外部变化、跨项目隔离和只读强行命令拒绝。报告：`artifacts/continuity/51c1fcae6e5e401a93dcfbfea83e3fb1/result.json`。
- `scripts/verify-host.py`：现有修复、实际 Python 验证、diff、两次审批和重启证据比对通过。报告：`artifacts/protocol-/24b5a5b53bbf4ce3a249e60e0d803c93/result.json`。
- `scripts/verify-lifecycle.py`：工作区排他、steering、停止、过期审批、崩溃、手动命令排他和繁忙时停止响应，共 **7 类检查通过**。报告：`artifacts/lifecycle/2585e122275149cf99fc97209f9ddf80/result.json`。
- 实际 XAML 的 Avalonia Headless 渲染：`artifacts/ui-composer/en-US-idle.png`、`zh-CN-running.png`、`zh-CN-narrow.png`。这些验证不启动 Host、不写用户设置、不打开桌面窗口；它们不能替代实机输入法和系统缩放测试。
- Codex 的图片、实际模型档位和三种 sandboxPolicy 在内存协议夹具中验证。未替用户登录，也未进行真实账户模型调用或验证各平台 OS 沙箱执行效果。
- 真实 DeepSeek 摘要/续接：对上述生成夹具的 SQLite 副本清除旧 checkpoint，强制重新摘要；使用当前 Core 与官方 SDK，单次运行 **3 次请求、8.114 秒、11,897 输入 / 771 输出 tokens**。1 次真实摘要、1 次文件读取，保留原始 34 条消息，最终 38 条；源数据库/文件不变，副本 checkpoint 与事件重启比对通过。任务明确要求核对外部修改并说明旧结果的证据边界，这是受控功能检查，不是自主任务成功率。报告：`artifacts/live-continuity/d15cf209431b47ed8bb7fcbc6776ed65/result.json`。

真实模型检查最初因 Python 把 CRLF 归一为 LF 而误报；`initial-result.json` 保留失败，随后只修正字节比较并使用既有响应离线复核，**没有再次调用模型**。脚本 `scripts/verify-live-continuity.py` 必须显式传 `--live-deepseek`，仅允许克隆生成夹具，不能拿用户真实会话库做该检查。

## 发布产物

最新包位于 `artifacts/publish/continuity-preview/`：win-x64 318 文件 / 252.42 MiB，linux-x64 314 / 142.26 MiB，osx-arm64 314 / 153.10 MiB。三个 SHA-256 清单通过，7 个源码依赖锁原始哈希全程未改变，每包 6 个生产依赖锁与源码一致；11 个原始许可/来源文件逐字节一致，无 Headless 文件或依赖。

Windows 包内 Host 使用自带运行时完成 Unicode 会话、实际命令和正常退出检查，0 次模型调用、没有启动 GUI。报告为 `artifacts/release-checks/continuity-preview.json` 与 `continuity-preview-supplement.json`。Linux/macOS 仅验证包结构、架构和哈希，未做实机运行。

## 尚未解决的范围

字符预算不是精确 tokenizer；所有原始用户要求最终仍可能超过预算，此时明确要求缩小任务或提高预算。运行次数/时间上限仍生效，不是无限自动续跑。崩溃前已经接收但未消费的 steering 仍以事件保留，需要审阅后继续；不自动执行未知结果的旧工具。

本地 skill 发现不包含远程安装、插件市场、任意脚本自动运行或全局用户目录；上传目前是图片，不是通用文档解析。经验检索限定同一工作区、近期 64 会话/128 候选，底层每个会话的事件读取尚未分页。没有固定预算的任务成功率、成本收益或对竞品胜率结论。

Linux/macOS 实机按用户安排暂缓；Windows 本轮也没有操作真实 GUI。公开论文收益不能直接套用到这些工程实现。
