**English** | [简体中文](README.zh-CN.md)

# Desktop presentation

Avalonia builds the Windows/Linux/macOS UI. Assembly names stay `Workbench.Desktop`; the product is Cogitide. The UI uses [Host](../Workbench.Host/README.md), never substitutes display state for observed execution or starts another agent kernel.

App/Program own startup/theme/lifetime; MainWindow owns projects/chats/composer/approvals/files/diff/output; ConnectionsWindow and ComposerChoices own providers, account/model/reasoning choices. HostClient/ProcessShutdown own stdio and owned process cleanup. TimelineEntry/DesktopEventPresentation/MarkdownMessage render bounded events/Markdown. WorkbenchTerminalWindow is the explicitly opened user terminal, separate from model noninteractive commands. Localization/LocExtension/Resources use ResourceManager/.resx; settings/identity/motion/glyph helpers own nonsecret preferences and display behavior.

[Provider setup](../Workbench.Core/Models/PROVIDER-CONFIGURATION.md) starts fresh installations with an explicit account/API choice. ConnectionsWindow accepts a masked API key for protected storage on the selected Host and fetches model metadata only on user action. Settings contain a credential reference rather than raw key material; configured older installations retain their authentication/model settings.

Settings reuse Avalonia's keyboard-accessible vertical `TabControl`: General, Models and accounts, Skills, Connections, and Advanced. Existing account/API controls retain their credential handling and live localization. Connections and task budgets route to the existing dialogs after settings close; research options remain explicit opt-ins. `ConnectionsWindow.Skills` lists project skills through `project.skills`, imports a folder containing `SKILL.md`, and searches/installs the Host's pinned public catalog only on user action. Imports use paths on the selected Host, so local folder picking is unavailable for remote Hosts. The desktop does not run skill scripts, add a package manager, or treat an unobserved install as success.

`SettingsNavigationTests` check the compiled XAML, language changes, preserved provider inputs, project scope and existing-dialog routes. Set `COGITIDE_SETTINGS_SCREENSHOT_DIR` to an absolute ignored artifact directory to capture fixture-only headless renderings with production styles; this is separate from real platform GUI acceptance.

The project/chat organization references Codex interaction patterns, without copying proprietary frontend code or assets. Controls/layout/animation/themes reuse **Avalonia**; code preview uses **AvaloniaEdit**; Markdown uses **Markdig**; terminals reuse **Iciclecreek.Avalonia.Terminal, Porta.Pty and XTerm.NET/ConPTY**. [DEPENDENCIES](DEPENDENCIES.md) and [package locks](packages.lock.json) record exact versions, fixed upstream commits and licenses.

Markdown does not enable HTML or automatic remote images; links open on user action. UI language/reduced motion do not alter original user/model/file text. A cross platform framework does not make every control a native system widget. Package builds, Headless tests and actual platform GUI acceptance remain separate evidence.

**Do not reinvent infrastructure.** Reuse controls, mature editor/Markdown/terminal/localization and Host contracts. Necessary drawing/interaction code needs a concrete component gap, alternatives and accessibility/scaling/IME verification; convenience never bypasses Host permissions or persisted state.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Long tasks and remote workspaces

`MainWindow.History` loads bounded presentation/message/event pages, shows pending input recovery, configures validated run budgets and explicitly continues stopped checkpoints. The live timeline is virtualized and retains at most 1,000 presentation entries; original durable records remain available through history pages. `MainWindow.Browser` uses server-side scoped file filtering and hash-bound preview pages. `RunBudgetWindow` reuses Avalonia inputs and the shared `RunLimits` contract.

`MainWindow.Remote` switches owned Host clients and clears endpoint-specific workspace/session/account caches. [Remote](Remote/README.md) implements nonsecret profiles and adapts system OpenSSH/WSL to the same stdio protocol. Local folder normalization is never applied to POSIX endpoint paths. Model/API credentials stay on that endpoint; current/recent custom key-variable names and common API-key variables are removed from local remote-transport child environments. The explicitly opened user terminal remains local; agent PTYs are available through the selected Host.

These adapters reuse Avalonia controls/virtualization, BCL Process/async/cancellation, official transport executables and existing Host/SQLite contracts. **Do not create another UI framework, SSH stack, wire parser, database or agent loop.** See [workflow validation and remaining bounds](../../docs/long-tasks-remote.md).
