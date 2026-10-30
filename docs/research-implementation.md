# 研究机制实现与公开发布验收

日期：2026-10-30。公开名 **Cogitide**，内部程序集/数据路径保留 Workbench。本文描述当前实现；旧验收文档和论文调研保留各自当时的状态。原始本地日志不发布到 Git，仓库保留可重复执行的测试与冻结夹具。

## 已实现的机制

| 论文思路 | 真实产品路径 | 项目增加部分 | 未声称完成的部分 |
| --- | --- | --- | --- |
| ACON | [Context](../src/Workbench.Core/Context/README.md)：历史摘要指南、工具观察投影；Host 显式失败驱动提案与成对选择 | 版本/哈希、宿主保留来源、原始事件不变、训练诊断隔离、最终留出测试 | 双 LLM compressor 全复现、蒸馏权重、原论文 benchmark |
| RepoMem / DCI | [RepositoryMemory](../src/Workbench.Tools/RepositoryMemory/README.md)：关键词与共同修改线索检索冻结 Git 历史 | 每次运行冻结 base commit、分页快照、物理路径/忽略规则、历史来源证据 | 完整学习语义图、预训练记忆、DCI 全代理体系 |
| GEPA | [SkillOptimization](../src/Workbench.Core/Research/SkillOptimization/README.md)：训练反馈 mutation、任务级 Pareto 候选、父代采样与可选合并 | 一个真实编码内核，固定仓库与测试，累计预算和可审阅产物 | 全 compound-AI 优化框架、作者完整 crossover、权重训练 |
| SkillOpt | 同一模块的另一条策略：有界修改与逐选择题不退步门控 | 不覆盖活动技能；选择冻结后才运行 holdout；保留拒绝候选和失败 | slow-update/meta-skill/Sleep 服务及完整作者 runner |
| Speculative Actions | [Speculation](../src/Workbench.Core/Research/Speculation/README.md)：搜索后预测一次只读文件读取，与推理重叠 | 精确参数、文件版本保护、运行级隔离、写入/追加指令取消 | 写入/命令推测执行、多分支或预测模型 |
| ACE / APC（此前基础） | [Memory](../src/Workbench.Core/Memory/README.md)：有来源和前置条件的增量经验、精确证据回读 | 来源事件、失效检测、不重放动作、用户要求保持 | 完整 generator/reflector/curator、自主持续学习与论文收益复现 |

Gaia2 用于启发异步/失败/恢复测试，不是可以直接安装的算法。本轮没有引入那些需要训练资源、私有基础设施或复杂多 agent 系统的整套研究。论文主张、会议级别与本项目实测分开；主来源和当前核查差异见 [论文复核](../research/papers-recheck-2026.md)。这里的“项目增加部分”属于工程设计与组合，尚未证明学术新颖性。

## 操作入口与边界

桌面实验设置和 `RunOptions.Research` 控制压缩指南、仓库历史与只读预取，默认全关。历史工具只有启用时注册，基础 17 工具变为 18；只读模式 12 变为 13。实验设置从下一次任务生效；版本文件/历史基点在任务开始冻结。

GEPA、SkillOpt 与指南优化通过 [Research CLI](../src/Workbench.Host/Research/README.md) 显式运行。`--validate-only` 不需密钥且不执行；`--execute` 使用同一 AgentEngine 和官方 SDK、固定 commit、独立工作区、冻结验证命令。验证文件直接写入/patch 拒绝；哈希变化时跳过验收，源仓库保持不变。选择集与最终留出集不能进入提案诊断；跨 split 的 family 与相同 payload 被拒绝。

所有普通推理、摘要和提案计入模型用量。预算在操作间检查，单个在途请求可能越过已观察额度；缺失用量标记未知，不当作免费。Ctrl+C/截止停止后续调用并留存状态。输出技能/指南不自动激活。执行器依赖可信本地验证脚本和显式允许的命令，**不是 OS 沙箱**。

## 验证记录

以下为最终源码修复后的发布前验证记录。功能验证只使用本地 SSE 与生成 Git 仓库，不调用外部付费模型、不占用桌面。

| 项目 | 当前结果 |
| --- | --- |
| 严格编译 | .NET 11 RC3 / C# 15，0 warnings / 0 errors |
| 单元/回归测试 | 最终 481 / 481 通过（含新增 Windows 快照竞争/永久错误/取消回归） |
| Host / Responses / lifecycle / continuity / tools | 5 组真实 stdio / 本地 SSE 集成全部通过；最终命令快照修复后重跑 lifecycle、tools 和 research 均通过 |
| Research 端到端 | 审查修复后通过：GEPA/SkillOpt 各 6 个真实评测、各 16 次已统计调用；ACON 4 次实际摘要、成对选择/最终留出；共 68 次本地请求、0 外部调用 |
| 三 RID 包结构 / Windows Host | win-x64 344 文件、linux-x64 340、osx-arm64 340 全部 SHA-256/架构/依赖锁/许可核对通过；Windows 打包 Host 在无系统 .NET 环境变量下运行、Unicode 命令/会话与正常退出通过；Linux/macOS 仅包结构验证 |

独立审查发现并修复：开发压缩诊断可伪装留出 ID、Windows 跨盘输出误拒、最后一次留出返回后取消仍可能写完成；新增相应回归。另一处有界哈希从流实际读取量限制，避免文件增长突破最初长度检查。最早一次测试失败来自临时 Git 对象的只读清理，另一次构建失败来自测试 Host 占用 DLL；没有将这些失败计为通过，最终检查改为串行。两轮全套还暴露后台 worker 的偶发 UnauthorizedAccessException，增加阶段诊断后又通过全套；随后本机以固定读取句柄确定复现 `File.Move(overwrite)` 的同型失败，并验证 BCL `File.Replace` 能处理同一场景，采用该原语替换状态快照并补确定回归。进一步全套暴露替换时路径短暂缺失/共享冲突，读取端只对 Windows 这两类打开失败做最多 310ms 的可取消重开；永久错误和损坏记录仍报错，不重试命令。没有证据确定原偶发错误的外部锁持有者；不将其断言为某个杀毒软件导致。对应上游记录：[dotnet/runtime #114230](https://github.com/dotnet/runtime/issues/114230)。

不将本地夹具中的候选胜出理解为真实模型能力提升。下一阶段需要同模型、同预算、互不重叠任务家族的真实仓库对照，报告成功率、介入次数、每成功任务成本、上下文/缓存开销和总延迟；目前没有这些优势结论。

## 模块与许可

[模块目录和文件数](module-map.md) 自动生成，项目按 Runtime / Models / Context / Memory / Projects / Persistence / Research / Workspace / Navigation / Editing / Execution / Auth 拆分。就近 README 写明成熟组件、固定上游、论文机制和改造范围；根 [AGENTS.md](../AGENTS.md) 与 [CONTRIBUTING.md](../CONTRIBUTING.md) 明确禁止不必要的重复造轮子。

原创代码 Apache-2.0；Pi/OpenCode 的 MIT 与 Codex 的原始 Apache/NOTICE 等全部保留。包中附根许可和第三方索引，具体见 [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)。

公开源码仓库：[Yang916-yy/cogitide](https://github.com/Yang916-yy/cogitide)。发布前使用校验过发行摘要的 Gitleaks v8.30.1 扫描待提交源码（redact），未发现泄漏；SDK、凭据、数据库与运行产物不纳入 Git。

本机证据入口（均在 Git 忽略的 artifacts 下）：`test-results/research-verified.trx`、`build-research-final.log`、`verification/research/ece91aabb563410987b6f7368b8db304/result.json`、`verification/research-packages.json`、`gitleaks-staged.json`。这些路径用于本机核验；公开用户应运行仓库脚本或查看 GitHub Actions 的独立报告。
