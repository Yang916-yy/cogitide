# Desktop component provenance

Checked against restored NuGet package metadata on 2026-10-30. Exact resolved dependencies and content hashes are recorded in `packages.lock.json`.

| Component | Version | Declared license | Package source revision |
|---|---|---|---|
| Iciclecreek.Avalonia.Terminal | 4.1.1 | MIT | [`55678b5681b1eeeaca8c57d136df32c01cc2b945`](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/tree/55678b5681b1eeeaca8c57d136df32c01cc2b945) |
| Porta.Pty | 2.2.3 | MIT | [`0186b604bf40161f199823fe5cdb2dfa02c37bea`](https://github.com/tomlm/Porta.Pty/tree/0186b604bf40161f199823fe5cdb2dfa02c37bea) |
| XTerm.NET | 3.0.0 | MIT | [`453ed957885f2e87c777e9b4f0b88a3582065eab`](https://github.com/tomlm/XTerm.NET/tree/453ed957885f2e87c777e9b4f0b88a3582065eab) |
| Microsoft.Windows.Console.ConPTY | 1.24.260710001 | MIT | [Microsoft Terminal](https://github.com/microsoft/terminal) (package metadata does not include a revision) |
| Avalonia.AvaloniaEdit | 12.0.0 | MIT | [AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit) |
| Markdig | 0.44.0 | BSD-2-Clause | [`cd7b9ca0ef66cb582232cbceaefbfe4195cf575b`](https://github.com/xoofx/markdig/tree/cd7b9ca0ef66cb582232cbceaefbfe4195cf575b) |

The first three license files were also checked at their package source revisions: [terminal](https://github.com/tomlm/Iciclecreek.Avalonia.Terminal/blob/55678b5681b1eeeaca8c57d136df32c01cc2b945/LICENSE), [PTY](https://github.com/tomlm/Porta.Pty/blob/0186b604bf40161f199823fe5cdb2dfa02c37bea/LICENSE), [emulator](https://github.com/tomlm/XTerm.NET/blob/453ed957885f2e87c777e9b4f0b88a3582065eab/LICENSE). They identify copyright 2025 Tom Laird-McConnell and use MIT terms. Distribution must preserve applicable copyright and license notices; this provenance table is not a substitute for those notices or a complete transitive dependency audit.

The terminal is opened explicitly by the user, runs in the selected workspace, and is independent of agent tool execution. Closing its window kills the owned process tree through .NET and disposes the PTY. Windows validation confirmed a real PowerShell prompt and shell process termination after closing. Linux/macOS PTY behavior, full-screen applications, Chinese IME, resize, descendant cleanup and clipboard behavior require platform acceptance testing.

The code viewer uses AvaloniaEdit with its Fluent theme and built-in syntax definitions. It is read-only. Assistant Markdown is parsed by Markdig and rendered with native Avalonia text, lists, tables and code blocks. HTML and automatic image/resource loading are disabled; web links open only after an explicit click. Code blocks support copying. The net9.0 Markdig asset used by this net11.0 project has no transitive dependencies.
