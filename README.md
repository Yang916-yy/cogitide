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

On a fresh installation, choose a ChatGPT account or API connection in Settings; no provider, endpoint or model is selected by default. Enter API keys in the masked field to save them in the selected Host's native credential vault, or retain named environment variables such as `DEEPSEEK_API_KEY`, `ANTHROPIC_API_KEY` or `OPENAI_API_KEY`. Address presets cover OpenAI, Anthropic, DeepSeek, OpenRouter and Mistral, with custom endpoints/protocols and manual model IDs. **Fetch models** is an explicit metadata-only request; a listing does not prove inference entitlement or model capabilities. See [provider setup and credential boundaries](src/Workbench.Core/Models/PROVIDER-CONFIGURATION.md).

DeepSeek, native Claude Messages, OpenAI-compatible Chat Completions and Responses share the kernel. Official ChatGPT OAuth supplies authentication to Responses; it does not delegate execution to another agent or read other applications' account files. Live account access remains a separate verification step.

## Coding workflow

- Project/chat sidebar, conversation timeline, files and diffs, image attachments, model/reasoning selection, execution mode and one send/stop button. Desktop localization includes English and Simplified Chinese, with reduced motion.
- Paged file reads, literal/regex/glob search, syntax outlines, **Roslyn semantic definitions/references/diagnostics**, version-checked multi-file patches and Git diff.
- **Multi-language LSP queries** for definitions, references, hover, diagnostics and document symbols through user-installed TypeScript/JavaScript, Python, C#, Go and Rust servers. Configuration and workspace trust are explicit; no language server is auto-installed. See [LSP setup and evidence limits](src/Workbench.Tools/Lsp/README.md).
- **Git review UI** for working/staged diffs, file/hunk stage, unstage and discard, and snapshot-anchored feedback added to the draft prompt. Commit, upstream push and GitHub pull request creation are separate user actions. Installed Git/`gh` own writes and authentication; see [review safeguards](src/Workbench.Tools/GitReview/README.md).
- Owned foreground/background commands with bounded output, polling, controlled stdin and real PTYs through Porta.Pty. Explicit session-lived development commands survive ordinary run completion until stopped/expired; Host loss and explicit cancellation stop owned resources.
- Durable task checkpoints and explicit continuation: default 256 iterations / six hours, configurable up to 10,000 / seven days, with optional reported-token caps. Budget stops pause the task. Requests are persisted before acceptance; original actions are never replayed.
- Bounded SQLite history pages, a virtualized rolling timeline, server-side scoped file filtering and snapshot-bound file/preview pages.
- **WSL and SSH workspaces:** system WSL/OpenSSH connect the same Host kernel, keeping model credentials and tools on the selected endpoint. The explicit guided deployer transfers a matching prebuilt self-contained Host into new private endpoint directories, so prior manual Host deployment is optional. **Durable service mode** retains accepted work and pending approvals across desktop/transport disconnection; foreground stdio mode stops on EOF. Nonsecret profiles, strict host-key checks and cursor-based recovery preserve unknown outcomes without replay. See [remote setup](src/Workbench.Desktop/Remote/README.md).
- **Optional native OS tool isolation** launches a restricted worker through the pinned Microsoft MXC SDK. Requiring isolation fails closed on unavailable assets, platform prerequisites or policy rejection; application approval modes remain separate. The pinned package has no Intel macOS (`osx-x64`) native asset, so that app target remains available with OS isolation unavailable. See [policy and native acceptance boundaries](src/Workbench.Tools/Isolation/README.md).
- Incremental compaction/checkpoints, original evidence, scoped `AGENTS.md`, on-demand skills and source-backed project experience. Interrupted actions are never automatically replayed.
- **MCP** through the official C# SDK, with stdio and Streamable HTTP transports. Each run owns fresh connections; remote tools are classified as approval-required and follow the selected execution policy (including explicit full-access/auto-approval grants). Put explicit configuration in the Host data directory's `mcp.json`; see [MCP setup](src/Workbench.Tools/Mcp/README.md).
- **Headless browser acceptance** through Playwright: text, CSS visibility, title and status assertions; console/page/network errors; optional screenshots. Each call gets a fresh browser context. Explicit origins are enforced and automatic HTTP redirects are rejected; see [browser checks](src/Workbench.Tools/Browser/README.md).

Source-only Roslyn navigation binds symbols across a bounded source scope without executing MSBuild or loading project generators/packages. The separately configured LSP integration uses external servers, which can execute project tooling and therefore require trusted workspaces and launch approval. Neither path claims complete IDE parity. See [Roslyn boundaries](src/Workbench.Tools/Navigation/README.md) and the [coding workflow guide](docs/development-workflow.md).

Execution modes are application permissions; native tool isolation is a separate explicit requirement. Native backend denial tests, portable package builds, headless UI tests and real desktop acceptance are separate evidence. Linux/macOS native desktop behavior and signed installers remain to be validated.

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

Run SDK commands and Host integration fixtures serially to avoid Windows DLL locks. Default tests use generated repositories and local protocol/model fixtures; they do not open the desktop or call paid models. [Current integration and performance results](docs/integrations-performance.md) separate actual measurements from unverified claims. [Long tasks and remote validation](docs/long-tasks-remote.md) records the current workflow and acceptance scope. [Measured allocation tuning](docs/performance-tuning.md) records matched sources, variable latency and withdrawn cache experiments. [Cross-domain tool designs](research/cross-domain-agent-optimization.md) distinguish proposed mechanisms from production capabilities. [Benchmark instructions](benchmarks/Workbench.Benchmarks/README.md) use BenchmarkDotNet and native .NET diagnostics.

## Documentation and contributions

English is the default documentation language. Existing detailed Chinese records are retained as adjacent `*.zh-CN.md` files with language links. Historical records keep their original dates and test counts.

- [Module inventory and file counts](docs/module-map.md)
- [Coding workflow, deployment and acceptance boundaries](docs/development-workflow.md)
- [Architecture roadmap](research/architecture-roadmap.md), [open-source review](research/opensource.md), [.NET reuse audit](research/dotnet-reuse-audit.md)
- [Release instructions](docs/release.md), [contribution rules](CONTRIBUTING.md), [repository instructions](AGENTS.md)

**Do not reinvent unnecessary infrastructure.** Check .NET/BCL, existing modules, official SDKs and mature upstreams first. Peripheral module READMEs identify reused components and exact adaptation sources; kernel READMEs map papers to implemented mechanisms and project-specific changes. Custom infrastructure requires a concrete gap and validation evidence.

Original contributions use [Apache-2.0](LICENSE). Adapted code and dependencies retain their own licenses. Preserve [NOTICE](NOTICE), [third-party notices](THIRD_PARTY_NOTICES.md) and module `Licenses/PROVENANCE.md` files.
