# Cogitide

English | [简体中文](README.zh-CN.md)

**A coding-first desktop agent built with C# 15, .NET 11 and Avalonia.** Cogitide combines *Cogito* with *tide*: sustained thinking that moves work forward. Internal assemblies and storage paths retain the `Workbench` name for compatibility.

Every model uses one agent kernel for tools, permissions, project rules, compaction, evidence, memory and recovery. Mature infrastructure comes from .NET, official SDKs and focused, attributed adaptations of Pi, OpenCode and Codex. This is an early product; research-inspired mechanisms have functional tests, not demonstrated benchmark superiority.

## Get started

Install Git and PowerShell 7.2+. Build scripts bootstrap the SDK pinned by `global.json` into `.tools/dotnet`.

```powershell
./scripts/build.ps1
./scripts/run.ps1
# Portable directory with the runtime; this does not open the GUI.
./scripts/publish.ps1 -Runtime win-x64
```

Tests require matching Playwright Chromium. `build.ps1` installs it when running tests; on Linux, first install its OS dependencies with `pwsh tests/Workbench.Tests/bin/Debug/net11.0/playwright.ps1 install --with-deps chromium` after a `-SkipTests` build. Browsers run headlessly with isolated contexts.

Configure providers in Settings. API credentials come from named environment variables such as `DEEPSEEK_API_KEY`, `ANTHROPIC_API_KEY` or `OPENAI_API_KEY`. DeepSeek, native Claude Messages, OpenAI-compatible Chat Completions and Responses share the kernel. Official ChatGPT OAuth supplies authentication to Responses; it does not delegate execution to another agent or read other applications' account files. Live account access remains a separate verification step.

## Coding workflow

- Project/chat sidebar, conversation timeline, files and diffs, image attachments, model/reasoning selection, execution mode and one send/stop button. Desktop localization includes English and Simplified Chinese, with reduced motion.
- Paged file reads, literal/regex/glob search, syntax outlines, **Roslyn semantic definitions/references/diagnostics**, version-checked multi-file patches and Git diff.
- Owned foreground/background commands with bounded output, polling and cancellation cleanup. Commands are noninteractive; running processes are stopped when their owning run ends.
- Incremental compaction/checkpoints, original evidence, scoped `AGENTS.md`, on-demand skills and source-backed project experience. Interrupted actions are never automatically replayed.
- **MCP** through the official C# SDK, with stdio and Streamable HTTP transports. Each run owns fresh connections; remote tools are classified as approval-required and follow the selected execution policy (including explicit full-access/auto-approval grants). Put explicit configuration in the Host data directory's `mcp.json`; see [MCP setup](src/Workbench.Tools/Mcp/README.md).
- **Headless browser acceptance** through Playwright: text, CSS visibility, title and status assertions; console/page/network errors; optional screenshots. Each call gets a fresh browser context. Explicit origins are enforced and automatic HTTP redirects are rejected; see [browser checks](src/Workbench.Tools/Browser/README.md).

Semantic navigation binds symbols across a bounded source scope, distinguishing overloads and same-name locals. It complements text search. It does not execute MSBuild or load project generators/packages, and is not a complete IDE/LSP implementation. See [navigation boundaries](src/Workbench.Tools/Navigation/README.md).

Execution modes are application permissions, not an OS sandbox. Linux/macOS native desktop behavior and signed installers remain to be validated. Portable package builds and headless CI are recorded separately from GUI certification.

## Optional research mechanisms

Runtime research settings are off by default. Offline optimization requires an explicit command and never activates a candidate automatically.

| Mechanism | Implemented slice |
| --- | --- |
| ACON-inspired compression | Observation projections, versioned history guidance, source anchors, failure-driven guideline proposals and paired evaluation |
| RepoMem / DCI-inspired retrieval | Frozen Git history, keyword and co-change evidence with commit/path sources |
| Speculative Actions | One bounded read prefetch overlapping model inference; exact request and fresh hash validation before use |
| GEPA | Reflective skill mutation, task-level Pareto candidate selection and optional merging |
| SkillOpt | Bounded skill updates, selection non-regression checks and held-out evaluation after selection is frozen |
| ACE/APC-inspired experience | Incremental, source-backed entries with prerequisites and stale-source detection |

These are independent, selective engineering implementations. Training pipelines, full author systems and published benchmark gains have not been reproduced. See [research implementation](docs/research-implementation.md) and [paper review](research/papers-recheck-2026.md).

```text
Workbench.Host --optimize-skill SPEC.json --output NEW_DIRECTORY --validate-only
Workbench.Host --optimize-skill SPEC.json --output NEW_DIRECTORY --execute
Workbench.Host --optimize-compression SPEC.json --output NEW_DIRECTORY --execute
```

Execution can incur provider charges; validation and default fixtures do not. Data splits, frozen validators, budgets and artifacts are described in [Research CLI](src/Workbench.Host/Research/README.md).

## Verification and performance

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

Run SDK commands and Host integration fixtures serially to avoid Windows DLL locks. Default tests use generated repositories and local protocol/model fixtures; they do not open the desktop or call paid models. [Current integration and performance results](docs/integrations-performance.md) separate actual measurements from unverified claims. [Benchmark instructions](benchmarks/Workbench.Benchmarks/README.md) use BenchmarkDotNet and BCL process diagnostics.

## Documentation and contributions

English is the default documentation language. Existing detailed Chinese records are retained as adjacent `*.zh-CN.md` files with language links. Historical records keep their original dates and test counts.

- [Module inventory and file counts](docs/module-map.md)
- [Architecture roadmap](research/architecture-roadmap.md), [open-source review](research/opensource.md), [.NET reuse audit](research/dotnet-reuse-audit.md)
- [Release instructions](docs/release.md), [contribution rules](CONTRIBUTING.md), [repository instructions](AGENTS.md)

**Do not reinvent unnecessary infrastructure.** Check .NET/BCL, existing modules, official SDKs and mature upstreams first. Peripheral module READMEs identify reused components and exact adaptation sources; kernel READMEs map papers to implemented mechanisms and project-specific changes. Custom infrastructure requires a concrete gap and validation evidence.

Original contributions use [Apache-2.0](LICENSE). Adapted code and dependencies retain their own licenses. Preserve [NOTICE](NOTICE), [third-party notices](THIRD_PARTY_NOTICES.md) and module `Licenses/PROVENANCE.md` files.
