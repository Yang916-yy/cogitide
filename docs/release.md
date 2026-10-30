# Cogitide 开发预览包

Cogitide 是公开项目名。程序文件仍叫 `Workbench.Desktop` 和 `Workbench.Host`，数据目录仍为 `Workbench`。

2026-10-30 研究增强预览包位于 `artifacts/publish/research-preview/{win-x64,linux-x64,osx-arm64}`。所有模型共用统一内核。基础工具 17 项，实验设置可额外启用 Git 历史检索、ACON 启发压缩和只读预取；离线技能/指南优化需显式启动，结果不自动生效。源码仓库的 `docs/research-implementation.md` 记录验收。真实 ChatGPT 账号授权与在线推理尚待验证，Linux/macOS 尚未实机运行。

请把整个发布目录放在本机磁盘上，保留所有 DLL、原生库和子目录。包内的 `publish-info.json` 记录目标架构、实际 .NET 运行时版本及是否自带运行时；默认发布为 self-contained，无需另装 .NET SDK。Git 和你项目的语言工具链需要按所用功能另外提供。无需安装 Codex CLI。

## 启动

| 包 | 启动方式 |
| --- | --- |
| `win-x64` / `win-arm64` | 双击 `Workbench.Desktop.exe`；使用 `start.cmd` 可明确绑定同目录 Host。 |
| `linux-x64` / `linux-arm64` | 在图形桌面的终端中运行 `sh ./start.sh`。 |
| `osx-arm64` / `osx-x64` | 在对应架构的 Mac 终端中运行 `sh ./start.sh`。 |

Shell 启动脚本会恢复两个程序可能在跨系统复制时丢失的执行权限。启动脚本把 `WORKBENCH_HOST_PATH` 指向本包中的 Host，防止开发机遗留环境变量误选其他版本。

默认会话和设置保存在当前用户本地应用数据目录下的 `Workbench`；账号凭据使用系统保护存储。更新时关闭程序，使用新的完整发布目录；原用户数据独立保留。旧 Codex 聊天可以查看，继续时会新建 Cogitide 聊天；旧登录文件不会读取或迁移，需要重新完成官方授权。开发测试需要隔离状态时可以传入 `--data-dir`，并用 `--workspace` 指定工作区，例如：

```powershell
.\start.cmd --data-dir "D:\Cogitide-Test-State" --workspace "D:\MyProject"
```

```sh
sh ./start.sh --data-dir "$HOME/Cogitide-Test-State" --workspace "$HOME/MyProject"
```

## 连接模型

API 提供方读取配置中指定的环境变量；默认是 `DEEPSEEK_API_KEY`。设置在本机后重新启动应用，或从已有该环境变量的终端启动。不要把密钥写入发布目录或项目文件。

ChatGPT 在“设置 → ChatGPT 账号”中点击“继续使用 ChatGPT”，浏览器打开官方授权页面。账号可以独立添加、选择与退出；模型目录按所选账号刷新。需要官方账号实际授予模型使用权限。Cogitide 直接调用公共 Responses API，模型任务由自身内核执行，不调用外部 Codex 程序。流程依据 [官方登录文档](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)。

Windows 使用 DPAPI，macOS 使用 Keychain，Linux 使用 Secret Service/libsecret。若系统凭据服务不可用，登录会明确报错；不会把令牌改存为明文。API 密钥还可用于 OpenAI Responses、Chat Completions、DeepSeek 和 Claude。

只读、工作区编辑与完全访问控制的是 Cogitide 工具和审批策略，当前没有操作系统沙箱。

后台命令属于当前任务，任务结束、取消或 Host 断开会停止尚未完成的命令。同一聊天后续可分页读取最多 4 MiB 的保留日志；不会自动重跑命令，不是跨任务常驻服务。多文件补丁先验证全部路径与内容哈希，受控失败尝试回滚，但不提供断电时的多文件原子事务。

## 平台范围

包内自带的 .NET 运行时不包含操作系统图形组件。Linux 仍需图形会话及 Avalonia/.NET 所需系统库，安装名称以发行版为准，参见 [Avalonia Linux 部署说明](https://docs.avaloniaui.net/docs/deployment/linux)。

当前构建流程未执行 Windows 应用签名或 macOS Developer ID 签名、公证，也未生成 macOS `.app` 安装包。macOS 对下载应用的检查与处理方式见 [Apple 官方说明](https://support.apple.com/en-us/102445)。

构建成功、校验和正确、Host 测试通过分别证明不同事项。跨平台发布不能代替目标机器的 GUI 验收；输入法、文件选择器、原生终端和窗口行为仍需要各平台实测。`publish-info.json` 的 GUI 验证字段不会因发布或 Host 测试自动改为通过。

## 校验与开发者发布

`SHA256SUMS` 列出包内文件哈希；Linux 可运行 `sha256sum -c SHA256SUMS`，macOS 可运行 `shasum -a 256 -c SHA256SUMS`。哈希清单用于发现缺失或改动，不等同于发行者签名。

在源码仓库中，可以检查包的原生架构、运行时组件、依赖锁及全部哈希，并选择运行本机 Host 的离线测试：

```powershell
python scripts/verify-release.py artifacts/publish/tools-upgrade-preview/win-x64 --smoke-host --require-source-locks --require-unified-kernel --require-tool-upgrade
python scripts/verify-tools-upgrade.py --host artifacts/publish/tools-upgrade-preview/win-x64/Workbench.Host.exe
pwsh scripts/build.ps1 -Configuration Release
pwsh scripts/publish.ps1 -Runtime win-x64 -OutputDirectory artifacts/publish/new-win-x64
```

`--smoke-host` 只在目标系统与当前机器匹配时执行，使用独立测试状态和工作区，不调用模型或登录账号。`--require-source-locks` 要求包内依赖锁与当前源码一致；`--require-unified-kernel` 检查鉴权/Responses 依赖、来源许可和旧执行器的移除；`--require-tool-upgrade` 额外核对 glob 组件及三组工具改编的来源许可。`verify-tools-upgrade.py` 使用本地 SSE 夹具驱动真实工具，不调用云端模型。检查历史版本可省略新版本检查选项。发布脚本拒绝覆盖非空目录。框架依赖包可加 `-FrameworkDependent`，这种包需要另装匹配的运行时。

## 许可证

原创代码 Apache-2.0；保留包内 `LICENSE`、`NOTICE`、`THIRD_PARTY_NOTICES.md` 和各模块 `Licenses`。第三方部分仍适用原始许可证。
