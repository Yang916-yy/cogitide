# 工具升级与后台验收记录

日期：2026-10-30，内部代号 Cogito。本轮补齐多文件补丁、分页导航和后台命令，并接入已有项目规则、证据与经验复用。严格构建为 **0 警告、0 错误**；新增大命令输出分页检查后的全量复跑 **366/366 通过**。首次全量运行有一次原因未明的 worker 提前退出，后续全量复跑及该用例连续 12 次定向复跑通过；这不构成已定位或修复该偶发问题的证明。

当前 Host 完整配置向模型提供 **17 个工具**；只读模式提供 **12 个**。计数包含内核注入的四个知识工具，不适用于缺少命令管理器或项目规则解析器的独立库调用。

| 分组 | 精确工具名称 | 只读模式 |
| --- | --- | --- |
| 文件导航（5） | `list_files`、`read_file`、`search_files`、`glob_files`、`inspect_symbols` | 全部保留 |
| 文件修改（3） | `write_file`、`replace_text`、`apply_patch` | 全部禁用 |
| 命令（4） | `run_command`、`start_command`、`read_command`、`stop_command` | 仅保留读取和停止所属命令 |
| Git（1） | `git_diff` | 保留 |
| 知识与连续性（4） | `inspect_evidence`、`read_skill`、`remember_experience`、`recall_project_memory` | 全部保留 |

只读是工具策略：不启动命令、不修改项目文件，仍可停止本聊天的已有命令和保存内部经验。它不是操作系统沙箱。API 与 ChatGPT 账号鉴权均使用同一 AgentEngine、工具、审批和摘要路径。

| 本轮能力 | 实现及边界 |
| --- | --- |
| 多文件补丁 | 支持 `*** Begin Patch` 的 Add/Update/Delete/Move、上下文 hunk 和 EOF 定位；已有源文件必须提供当前 SHA-256，新目标不得覆盖。所有文件先预检，提交前再次核对；受控失败逆序回滚，不能恢复时明确保留恢复文件。规则检查覆盖每个源路径及移动目标，审批后再次检查。 |
| 精确修改 | 上下文必须唯一、精确匹配，拒绝歧义和过期哈希；不采用宽松空白或标点替换。保留已有 UTF-8 BOM、换行形式及末尾换行状态。最多 64 个源操作、512 个 hunk、1 MiB/文件、原始与新内容合计 8 MiB；结果元数据超限在写入前拒绝。断电或进程崩溃不保证多文件原子性。 |
| 分页导航 | 读取最多 16 MiB 的 UTF-8 文件，每页最多 12,000 字符，支持超长行的行/字符双游标与完整文件哈希。检索增加正则、glob、上下文和快照校验；默认仍为大小写不敏感的字面检索。每次扫描最多 2,000 文件/32 MiB，正则每行 100 ms 超时；必须检查截断字段并缩小范围。C# 符号工具是 Roslyn 语法轮廓，不是 LSP 语义分析。 |
| 后台命令 | `start_command` 返回所属聊天/工作区的 ID，`read_command` 用 UTF-8 字节游标读取最多 4 MiB 的保留日志；运行、终止、失败和失联明确区分。停止、运行结束或父管道断开时清理自有进程；未完成命令或清理失败不能产生正常完成状态。重启只读历史日志，不按旧 PID 重连，也不重放命令。没有交互式 PTY、跨 Host 进程恢复或任意脱离进程树的后代约束保证。 |
| 证据与经验 | 多文件哈希及删除状态一起持久化，移动同时记录原路径删除和目标状态；召回重新核对源事件、全部目标及删除路径。命令零退出只是观察，不能证明任务成功。修复保存/召回预算不一致：保存前校验完整单条召回包可装入 16,000 字符预算；经验始终是候选，不自动执行。 |
| 审查修正 | 清理路径无条件尝试停止资源，Host 无条件释放聊天/工作区占用；拒绝缺字段或终态矛盾的命令记录。输出 sink 失败先停止进程，分页、保留及内核截断均避免切断代理项对。 |

后台验证使用生成的临时工作区、假模型/本地 HTTP 或 SSE 夹具及真实 Host stdio。未调用付费模型、未进行真实账号 OAuth 授权，未打开或接管用户桌面；无界面测试覆盖不等于可见 GUI 实机验收。当前实际进程验证在 Windows 完成。

| 验证 | 结果与原始记录 |
| --- | --- |
| 严格构建 | 通过，0 警告/0 错误：[构建日志](../artifacts/build-tools-upgrade.log)。 |
| 全量测试 | 先前 364 通过/1 失败：[初次 TRX](../artifacts/test-results/tools-upgrade-final.trx)；复跑 365/365：[早期复跑](../artifacts/test-results/tools-upgrade-final-rerun.trx)。新增大命令用例后全量 366/366：[中间 TRX](../artifacts/test-results/tools-upgrade-complete.trx)；最终源码严格构建后再次 **366 通过/0 失败/0 跳过**：[发布前 TRX](../artifacts/test-results/tools-upgrade-release.trx)。 |
| 偶发退出跟进 | `WorkerLosesParentPipeAndCancelsRatherThanDetaching` 首次在获取命令 PID 前观察到 worker 无终态退出，状态为 `interrupted`，没有误报成功。随后该用例连续 12 次通过，记录为 `artifacts/test-results/worker-start/worker-start-1.trx` 至 `worker-start-12.trx`。已增加启动失败的异常类型/HResult 诊断，原始失败原因仍未确定。 |
| 大命令/输出边界 | 命令元数据超限在执行前拒绝；响应超过 80,000 字符时减少本页输出并调整字节游标，后续可无损取回。新增用例首次出现空输出（[记录](../artifacts/test-results/tools-upgrade-verified.trx)），补充终态断言后定向及全量复跑通过；该次空输出原因未捕获。另修正 worker 完成后被移出注册表时的读状态竞态，不把这项修正当作两个偶发观测均已定位的证明。 |
| 新工具真实 Host 链路 | 通过：3 页文件读取、13 页命令输出、批准的双文件补丁、修改结果验证、硬杀 Host 后 shell 及两个生成的后代退出、冷启动无重放、只读 12 工具与旧日志读取。[结果](../artifacts/verification/tools-upgrade/d8345a2b1d534d6eaf252e4d12b6769b/result.json)。 |
| 最终 Windows 独立包工具链路 | 最终源码发布后通过全部 9 项检查：26 次本地模型请求、3 页文件、12 页 Unicode 命令日志、3 次精确审批、双文件真实验证、硬杀父 Host 后两级后代退出、冷启动保留日志而不重放。记录同时保存 Host/Core/Tools 等托管程序集哈希，避免仅凭未变化的原生启动器误认版本。[最终包结果](../artifacts/verification/tools-upgrade/41e34c77e206412c86defe4a4baeb354/result.json)。 |
| 长任务连续性 | 通过：7 次摘要、保留原始约束与完整工具组、检查点重启、作用域规则变化、skills、跨会话经验、过期检测和工作区隔离。[结果](../artifacts/continuity/14c9ab7a7f7c415ab065476f6675a5a8/result.json)。 |
| 原协议回归 | 通过：文件读取/修改、命令、diff、证据、审批和重启检查。[结果](../artifacts/protocol-/d311f8026fae42a7b6479bd8a43445ee/result.json)。 |
| 原生 Responses 回归 | 通过：5 次本地模型请求、两次审批、自有工具事件、冷恢复和原生状态回放；PATH 中没有 Codex，无 Codex 程序集依赖，私有 reasoning 不进入事件。[结果](../artifacts/verification/unified-responses/fce6e67dc88440bc97ca5c89f0b57492/result.json)。 |
| 运行生命周期 | 通过：工作区所有权、写入前追加指令、审批取消/过期拒绝、崩溃恢复无重放、手动命令所有权，以及 16 个工作槽占用时仍可停止。[结果](../artifacts/lifecycle/05ef598c356d41c1965761de601117fd/result.json)。 |
| 新版分发产物 | `tools-upgrade-preview/{win-x64,linux-x64,osx-arm64}` 三包核验通过，分别 341/337/337 个文件哈希，每包 6 份匹配源依赖锁；运行时、原生架构、glob 组件与三组工具许可核对通过。Windows Host 实际加载包内运行时，Unicode 会话/命令与正常退出通过。[发布检查](../artifacts/release-checks/tools-upgrade-preview-final.json)。Linux/macOS 仅结构/哈希检查，未实机运行。早期候选包另存于 `tools-upgrade-candidate`。 |

成熟机制的改编范围、固定源码及原始许可证均已记录：

- [补丁来源](../src/Workbench.Tools/Licenses/Patch/PROVENANCE.md)：OpenCode `810c0eba1a7348036f8bdf61e6632d0c36c6876c` 的补丁模块局部 C# 移植。
- [导航来源](../src/Workbench.Tools/Licenses/Navigation/PROVENANCE.md)：OpenCode `60072747957926e7aeec7e911fe6e23abe9bf830` 的读取/检索/glob 行为，复用 Microsoft.Extensions.FileSystemGlobbing 10.0.14、BCL Regex 和文件 I/O。
- [命令来源](../src/Workbench.Tools/Licenses/Commands/PROVENANCE.md)：Codex `788b9d7bf6e37f735647b7f84b556c2a807df4d8` 的 unified exec 行为参考；执行与父管道所有权由 .NET `Process`、流和取消机制实现，没有复制整套 Rust runtime。
- [开源调研](../research/opensource.md)与 [.NET 复用审查](../research/dotnet-reuse-audit.md)：依赖、版本、没有移植的部分和适用边界。工程固定 .NET 11 RC3 / C# 15；已有组件复用不能替代本产品的性能基准，尚未给出速度或内存收益数字。

这些结果证明当前本地链路及回归检查通过，不代表已达到 Codex/OpenCode 的整体任务成功率。交互式终端、完整 LSP、子 agent、网页检索和 MCP 扩展仍不在这 17 个内建工具内。

论文复核见 [2026 Oral/Spotlight 与实验可信度](../research/papers-recheck-2026.md)。本轮核实 GEPA/Gaia2/Speculative Actions 的 ICLR Oral 身份，ACE/RepoMem 为 Poster；新增 DCI、SkillOpt 的 NeurIPS Spotlight 官方记录。确定性工具与故障夹具只是工程基础，不等于复现论文收益；后续按精确搜索与证据、增量经验/摘要、离线技能优化的顺序做单项对照，尚未启动付费评测。

CI 已增加 Responses、连续性及工具升级集成步骤，继续保留 Windows/Linux/macOS 矩阵；本轮没有触发远程 CI，因此配置存在不算三端运行通过。
