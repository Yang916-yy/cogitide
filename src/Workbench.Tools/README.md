**English** | [简体中文](README.zh-CN.md)

# Tools: C# adapters for mature contracts

`WorkspaceTools.cs` assembles definitions. [Workspace](Workspace/README.md) owns checked paths and BCL file/process operations; [Navigation](Navigation/README.md) reuses Git, Roslyn, regular expressions and official globbing; [Editing](Editing/README.md) adapts OpenCode's Codex style patch format; [Execution](Execution/README.md) implements Codex inspired command lifetimes with .NET processes and optional Porta.Pty terminals. [Worktrees](Worktrees/README.md) adds managed isolated checkouts through Git CLI. New optional integrations are described separately in [MCP](Mcp/README.md) and [headless browser acceptance](Browser/README.md).

[Skills](Skills/README.md) reuses the existing project manifest parser for bounded local imports and fixed-revision public-source installation. [Presentations](Presentations/README.md) delegates editable PowerPoint packages and validation to the official Open XML SDK. Both preserve workspace permissions and applicable rules rather than introducing another agent or document/package engine.

[RepositoryMemory](RepositoryMemory/README.md) is an explicit research option: frozen revision Git history search with source evidence, inspired by RepoMem/DCI. Historical evidence never replaces reading current source.

[Presentations](Presentations/README.md) generates bounded editable PowerPoint packages with Microsoft's Open XML SDK. It shares checked workspace paths, edit locks, current-file hashes and approval gating. SDK package validation is separate from rendered visual verification.

Fixed source files, commits, adaptation boundaries and original licenses live in `Licenses/` and [DEPENDENCIES](DEPENDENCIES.md). Examples are OpenCode patch commit `810c0eba1a7348036f8bdf61e6632d0c36c6876c`, OpenCode navigation commit `60072747957926e7aeec7e911fe6e23abe9bf830` and Codex unified exec reference `788b9d7bf6e37f735647b7f84b556c2a807df4d8`. Reused components include `System.Text.Json`, `Process`, `FileStream`, SHA-256, Git CLI, Microsoft.CodeAnalysis and Microsoft.Extensions.FileSystemGlobbing.

**Do not reinvent infrastructure.** Inspect BCL, existing workspace primitives and mature implementations before adding a tool. Custom infrastructure needs a concrete gap, alternatives and validation; an upstream feature list is not a reason to import its whole runtime.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.
