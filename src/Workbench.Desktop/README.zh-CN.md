[English](README.md) | **简体中文**

# 桌面外围界面

本项目用 **Avalonia** 构造 Windows/Linux/macOS 共用界面，技术程序集名仍为 `Workbench.Desktop`，公开产品名为 Cogitide。它通过 [Host](../Workbench.Host/README.md) 执行任务，不能以控件显示状态替代真实工具结果或另建 agent 内核。

| 文件/组 | 职责 |
| --- | --- |
| `App.axaml`、`App.axaml.cs`、`Program.cs` | 应用入口、主题和生命周期。 |
| `MainWindow.axaml`、`MainWindow.axaml.cs` | 项目/会话、对话、输入区、审批、文件/diff/输出工作区。 |
| `ConnectionsWindow.axaml`、`ConnectionsWindow.axaml.cs`、`ComposerChoices.cs` | API/官方账号连接设置、模型与思考档位选择。 |
| `HostClient.cs`、`ProcessShutdown.cs` | 与 Host 的 stdio 通信及所属进程结束处理。 |
| `TimelineEntry.cs`、`DesktopEventPresentation.cs`、`MarkdownMessage.cs` | 事件时间线和有界 Markdown 呈现。 |
| `WorkbenchTerminalWindow.cs` | 用户显式打开的交互终端，和模型非交互命令分开。 |
| `Localization.cs`、`LocExtension.cs`、`Resources/` | ResourceManager/.resx 多语言资源。 |
| `DesktopSettings.cs`、`ProductIdentity.cs`、`UiMotion.cs`、`UiGlyph.cs` | 非秘密偏好、品牌、动画和图形展示辅助。 |

## 组件复用和界面来源

简洁的项目/会话及对话组织参考 Codex 的使用方式，这是交互设计参考，**没有复制其专有桌面前端源码或素材**。基础控件、布局、动画、主题复用 Avalonia；代码查看复用 **AvaloniaEdit**；Markdown 使用 **Markdig** 解析后由 Avalonia 控件呈现；终端复用 **Iciclecreek.Avalonia.Terminal、Porta.Pty、XTerm.NET/ConPTY**。精确版本、上游 commit 和许可证见 [DEPENDENCIES](DEPENDENCIES.md) 与 [依赖锁](packages.lock.json)。

HTML 和自动远程图片加载不因 Markdown 内容启用；链接由用户点击打开。系统语言和减少动画偏好与模型/文件原文区分。Avalonia 是跨平台 UI 框架，不等于三平台所有控件都采用系统原生实现；构建包、Headless 测试和真实平台 GUI 体验分别验收。

**严禁重复造不必要的轮子。** 禁止另写文本编辑器、Markdown parser、终端模拟器、国际化框架或模型客户端；优先使用已有控件/组件和 Host 协议。必要自绘或交互逻辑必须在 PR 说明现有组件无法满足的具体需求、可访问性/缩放/输入法验证及替代方案。不得为了界面方便绕过 Host 的权限和持久状态。
