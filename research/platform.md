**English** | [简体中文](platform.zh-CN.md)

# C#/.NET 11 platform and engineering choices

Historical platform research dated **2026-10-30**, without SDK installation, prototype builds, performance tests or three platform execution in that research round. Recommendations were judgments backed by the retained primary references, not completed acceptance. Later implementation status is separate.

The verified SDK baseline was **11.0.100-rc.3.26522.148**, .NET 11 RC3 released **2026-10-25**, Go-live but not GA; expected GA was November 2026. Pin global.json rather than floating latest. C# 15 union/closed hierarchies can express internal events/outcomes but IPC/storage need explicit discriminators/versions. Runtime Async, newer process/JSONL contracts and AOT changes require workload measurements. Task/async/cancellation/channels/DI/source generation/AOT already existed; memory safety remained explicit preview, not justification for unsafe business code.

Avalonia **12.1.4** was preferred for Windows/Linux/macOS; Uno was a backup needing its .NET 11/native embedding spike. MAUI/Mac Catalyst lacked official Linux desktop; WPF/WinUI were Windows only; Electron/.NET worker offered web richness at a second runtime cost; a custom Blazor/WebView shell moved platform compatibility into the core dependency. Avalonia's native Wayland option, accessibility and open source WebView claims were framework documentation, not proof of app IME/scale/screen reader/browser behavior. Exact package/native Linux fallback combinations, third party commercial components and licenses needed real checks. Compiled bindings/JSON metadata keep options open without assuming all dynamic plugins are AOT compatible.

Use official/mature .NET components before custom infrastructure: Microsoft.Extensions.AI model abstractions with preserved vendor extensions; Microsoft Agent Framework **1.0 GA 2026-04-02** as a comparable optional adapter rather than simultaneously stacking Semantic Kernel/AutoGen; official MCP C# SDK **2.0 released 2026-07-28** with protocol negotiation and separately versioned Apps/Tasks. Preserve own session/schema/authorization/recovery even if adopting a framework. SQLite, standard hosting/logging/telemetry and official provider SDKs were candidate foundations; only verified versions should be pinned at implementation time.

The six proposed layers were contracts/core/infrastructure/tools/Host/Desktop plus tests, split into assemblies only as dependencies required. A single controlled writer, immutable events, original evidence, hash based edits, budgets, clear unknown outcomes and cancellation remain product semantics. Independent process isolation and checked file APIs are not an OS sandbox.

Terminal candidates were Porta.Pty (ConPTY/forkpty), native Iciclecreek.Avalonia.Terminal/XTerm.NET, with xterm.js+existing PTY as fallback and node-pty as a mature comparison only if worth another runtime. Source main versions were not necessarily published NuGet versions. Tests needed CJK/emoji/IME, alternate screen/paste/scroll/resize, vim/tmux, native RID packaging and process cleanup. AvaloniaEdit was preferred for bounded code preview/light editing; Monaco was a WebView/worker heavy fallback, not a parallel default editor. DiffPlex could supply non Git display models, never replace Git index/rename/patch truth. Framework MIT licenses did not eliminate fonts/grammars/transitive native notices.

Full desktop automation was deferred to separate Windows UIA, macOS Accessibility/screen permissions and Linux AT-SPI/Wayland portal adapters; a cross platform GUI does not provide cross platform computer control. The first acceptance gate required actual platform GUI/IME/scale/editor/terminal/Host lifecycle, model/stream/cancel/persistence/permission and deployment checks. Startup/peak memory/UI latency/CPU/token/cost/correctness must be measured under fixed real workloads; paper or runtime microbenchmarks do not prove product speed or task quality.

This English page summarizes the preserved source. [The full Chinese record](platform.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)
- [Original reference 2](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/overview)
- [Original reference 3](https://devblogs.microsoft.com/dotnet/dotnet-october-2026-oob-security-updates/)
- [Original reference 4](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15)
- [Original reference 5](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime)
- [Original reference 6](https://docs.avaloniaui.net/docs/platform-specific-guides/linux)
- [Original reference 7](https://platform.uno/docs/articles/getting-started/requirements.html)
- [Original reference 8](https://learn.microsoft.com/en-us/dotnet/maui/supported-platforms)
- [Original reference 9](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)
- [Original reference 10](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
- [Original reference 11](https://www.electronjs.org/docs/latest/)
- [Original reference 12](https://github.com/AvaloniaUI/Avalonia/releases/tag/12.1.4)
- [Original reference 13](https://docs.avaloniaui.net/docs/app-development/accessibility)
- [Original reference 14](https://avaloniaui.net/blog/the-avalonia-webview-is-going-open-source)
- [Original reference 15](https://docs.avaloniaui.net/docs/app-development/embedding-web-content)
- [Original reference 16](https://docs.avaloniaui.net/tools/faq)
- [Original reference 17](https://docs.avaloniaui.net/docs/xaml/compilation)
- [Original reference 18](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [Original reference 19](https://platform.uno/docs/articles/features/using-skia-hosting-native-controls.html)
- [Original reference 20](https://learn.microsoft.com/en-us/dotnet/ai/microsoft-extensions-ai)
- [Original reference 21](https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-at-build-2026-announce/)
- [Original reference 22](https://devblogs.microsoft.com/dotnet/announcing-v20-of-the-official-mcp-csharp-sdk/)
- [Original reference 23](https://github.com/modelcontextprotocol/csharp-sdk)
- [Original reference 24](https://devblogs.microsoft.com/dotnet/routing-and-failover-for-microsoft-extensions-ai/)
- [Original reference 25](https://playwright.dev/dotnet/docs/intro)
- [Original reference 26](https://playwright.dev/dotnet/docs/browsers)
- [Original reference 27](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)
- [Original reference 28](https://github.com/tomlm/Porta.Pty)
- [Original reference 29](https://github.com/tomlm/Porta.Pty/blob/main/src/Porta.Pty/IPtyConnection.cs)
- [Original reference 30](https://github.com/tomlm/Porta.Pty/blob/main/src/Porta.Pty/Porta.Pty.csproj)
- [Original reference 31](https://github.com/tomlm/Porta.Pty/blob/main/LICENSE)
- [Original reference 32](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/blob/main/src/Iciclecreek.Avalonia.TerminalWindow/Iciclecreek.Avalonia.Terminal.csproj)
- [Original reference 33](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/blob/main/LICENSE)
- [Original reference 34](https://github.com/tomlm/XTerm.NET/blob/main/LICENSE)
- [Original reference 35](https://github.com/xtermjs/xterm.js/blob/master/LICENSE)
- [Original reference 36](https://github.com/microsoft/node-pty)
- [Original reference 37](https://github.com/microsoft/node-pty/blob/main/LICENSE)
- [Original reference 38](https://github.com/AvaloniaUI/AvaloniaEdit)
- [Original reference 39](https://github.com/AvaloniaUI/AvaloniaEdit/blob/master/LICENSE)
- [Original reference 40](https://github.com/AvaloniaUI/AvaloniaEdit/blob/master/Directory.Packages.props)
- [Original reference 41](https://github.com/mmanela/diffplex)
- [Original reference 42](https://github.com/mmanela/diffplex/blob/master/License.txt)
- [Original reference 43](https://github.com/microsoft/monaco-editor)
- [Original reference 44](https://github.com/microsoft/monaco-editor/blob/main/LICENSE.txt)
- [Original reference 45](https://developer.apple.com/documentation/screencapturekit)
- [Original reference 46](https://support.apple.com/en-ca/guide/security/secddd1d86a6/web)
- [Original reference 47](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.RemoteDesktop.html)
