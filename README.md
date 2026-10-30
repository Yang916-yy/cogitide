# Cogitide

**A coding-first desktop agent built with C# 15, .NET 11 and Avalonia.**

Cogitide 将 *Cogito*（思考）与 *tide*（潮汐）结合。中文说明为主，桌面支持简体中文和 English。公开项目名为 Cogitide；`Workbench` 继续作为程序集、协议和数据目录名称，保留既有会话兼容性。

这是可运行的早期开发版本。所有模型使用同一个自持 agent 内核，登录只负责鉴权。成熟外围优先使用 .NET 组件与官方 SDK，或从 Pi、OpenCode、Codex 的固定版本做有明确边界的改编；核心研究能力提供可关闭的基线与可核对的实验记录。

## 启动

需要 Git 和 PowerShell 7.2+。脚本按 `global.json` 安装项目私有 .NET 11 RC3 SDK，不替换系统 SDK。

```powershell
./scripts/build.ps1
./scripts/run.ps1
# 生成含运行时的便携目录，不启动桌面
./scripts/publish.ps1 -Runtime win-x64
```

发布支持 `win-x64`、`linux-x64`、`osx-arm64` 等 RID；启动 `start.cmd` / `start.sh`。Linux/macOS 原生桌面、输入法与进程行为仍待实机验收；macOS 产物尚未签名、公证或封装为 `.app`。三端后台 CI 与编译不等于桌面兼容认证。

设置中选择 API 服务与密钥环境变量（例如 `DEEPSEEK_API_KEY` / `ANTHROPIC_API_KEY` / `OPENAI_API_KEY`），密钥不写入配置。支持 DeepSeek、Claude 原生 Messages、OpenAI-compatible Chat Completions、Responses。ChatGPT 有独立官方 OAuth 登录入口，授权后直接调用 Responses，使用当前账号可用模型；不需要 Codex CLI，不读取其他应用的账号文件。真实账号权限与在线调用仍需实际登录验证。

## 已实现

- 简洁桌面工作流：项目与会话侧栏、对话时间线、文件与 diff、图片上传、模型/思考程度/执行模式、发送与停止共用按钮、多语言及减少动画选项。
- 统一 agent 工具：17 项基础工具，只读模式 12 项；分页读文件、正则/glob 搜索、Roslyn 符号、多文件 patch、Git diff、同步与后台命令。后台命令有所有权、输出游标和取消清理。
- Pi/OpenCode 风格自动摘要：完整工具轮次、原始用户要求、增量 checkpoint、原始证据与冷启动续接；不会自动重放旧动作。
- 项目 `AGENTS.md`、按需 skills、带来源与文件版本的经验存储/召回、原始证据回读。模型原生 continuation 和缓存/推理用量按提供方保存。
- 所有提供方共用工具、规则、摘要、审批和恢复。网络复用 .NET `SocketsHttpHandler` 与官方 SDK；状态使用 SQLite。

当前执行模式是应用权限控制，**不是操作系统沙箱**。命令不是交互终端；任务结束/Host 断开会清理仍在运行的受管命令。浏览器自动化、MCP、完整 IDE、多 agent 编排及完整办公套件尚未实现。

## 本轮研究能力

桌面“设置 → 通用 → 实验能力”可启用前三项，默认关闭。离线优化只能显式启动，不会在聊天期间自动收费或安装候选。

| 机制 | 已接入行为 | 边界 |
| --- | --- | --- |
| ACON 启发压缩 | 工具观察投影、版本化历史摘要指南、来源附录；开发失败驱动指南提案与成对评测 | 保留完整原始记录；没有蒸馏训练 |
| RepoMem / DCI 启发检索 | 冻结 Git revision 的历史文本与共同修改检索，返回路径/commit 来源 | 可选第 18 项工具 `repository_memory`；不是完整语义图或预训练记忆 |
| Speculative Actions | 搜索后与模型推理重叠的一次只读预取，参数/路径/哈希匹配才消费 | 不推测写入/命令；收益尚未测定 |
| GEPA | 训练反馈反思、候选 mutation、任务级 Pareto 选择与可选合并 | 单个技能的离线搜索，无权重训练 |
| SkillOpt | 有界技能更新、逐选择题不退步门控、冻结选择后最终留出测试 | 独立工作区和冻结测试；只导出供审阅 |

采用论文机制不等于复现论文效果。现在验证的是正确接入、隔离、预算和取消，尚无真实 benchmark 的成功率、成本或延迟优势结论。详见 [实现、验证和未实现部分](docs/research-implementation.md) 与各核心模块 README。

```text
Workbench.Host --optimize-skill SPEC.json --output NEW_DIRECTORY --validate-only
Workbench.Host --optimize-skill SPEC.json --output NEW_DIRECTORY --execute
Workbench.Host --optimize-compression SPEC.json --output NEW_DIRECTORY --execute
```

配置、数据分割、真实执行与预算语义见 [Research CLI](src/Workbench.Host/Research/README.md)。`--execute` 可能消耗模型额度；默认测试全部使用本地夹具。

## 验证与贡献

```powershell
./scripts/build.ps1
python scripts/verify-host.py
python scripts/verify-responses.py
python scripts/verify-lifecycle.py
python scripts/verify-continuity.py
python scripts/verify-tools-upgrade.py
python scripts/verify-research.py
python scripts/module-map.py --check
```

运行 SDK 命令与 Host 集成脚本时应串行，避免 Windows DLL 文件锁。默认不打开桌面、不调用付费模型。历史实测与当前验证分开记录，见 [最新实现验证](docs/research-implementation.md)；原始本地产物在被 Git 忽略的 `artifacts/`，CI 上传独立测试报告。

**严禁重复造不必要的轮子。** 新贡献先查 BCL、已接入的 SDK/模块和成熟上游。外围 README 写清改编来源与复用组件，核心 README 写清论文机制、项目增加部分及验证限制。例外需说明现有方案的具体缺口，见 [贡献规则](CONTRIBUTING.md) 与 [仓库指令](AGENTS.md)。

## 阅读入口

- [模块目录与文件数](docs/module-map.md)：自动生成，每个模块就近提供 README。
- [研究实现与验证](docs/research-implementation.md)、[论文复核](research/papers-recheck-2026.md)。
- [开源实现调研](research/opensource.md)、[.NET 复用审查](research/dotnet-reuse-audit.md)、[架构路线](research/architecture-roadmap.md)。
- [发布说明](docs/release.md)、[历史工具升级验收](docs/tools-upgrade-validation.md)。

项目原创代码使用 [Apache License 2.0](LICENSE)。改编部分与依赖保留原始许可证；具体来源、固定 commit 与发布告知见 [NOTICE](NOTICE)、[第三方来源索引](THIRD_PARTY_NOTICES.md) 及模块中的 `Licenses/PROVENANCE.md`。
