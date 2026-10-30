# Third-party sources and notices

Cogitide's original code is Apache-2.0. This does not replace the licenses of adapted code, dependencies, fonts or other third-party assets. Retain this index, `LICENSE`, `NOTICE` and the upstream notices when redistributing source or packages. This project is independent of the referenced projects and model vendors.

| Area | Source / component | Scope and retained notices |
| --- | --- | --- |
| History compaction | Pi and OpenCode, MIT | Focused C# adaptations of cut points, summary templates and context reservation; [fixed revisions and licenses](src/Workbench.Core/Licenses/Compaction/PROVENANCE.md) |
| Project rules and skills | Pi and OpenCode, MIT | Discovery and scope behavior; [source map](src/Workbench.Core/PROJECT-REUSE.md) |
| Responses state | Pi, MIT; official OpenAI .NET SDK | Selected replay/state behaviors and SDK integration; [provenance](src/Workbench.Core/Licenses/Responses/PROVENANCE.md) |
| Patch editing | OpenCode, MIT; Codex format reference | Focused port of OpenCode's Codex-style patch module; [exact source and original MIT notice](src/Workbench.Tools/Licenses/Patch/PROVENANCE.md) |
| Background commands | OpenAI Codex, Apache-2.0 | Behavioral reference, independently implemented with .NET processes; [provenance and original notice](src/Workbench.Tools/Licenses/Commands/PROVENANCE.md) |
| Interactive command terminals | Porta.Pty 2.2.3, MIT | Existing mature ConPTY/Unix PTY backend reused by Tools and Desktop; [fixed source and retained license](src/Workbench.Tools/Licenses/Commands/Porta-Pty-LICENSE.txt) |
| Remote workspaces | System OpenSSH and WSL; .NET Process | Existing authenticated stdio transports, no copied SSH/network implementation; [configuration and boundaries](src/Workbench.Desktop/Remote/README.md) |
| Navigation | .NET regular expressions, Microsoft.Extensions.FileSystemGlobbing, Roslyn and Git | [Components and licenses](src/Workbench.Tools/Licenses/Navigation/PROVENANCE.md) |
| Official account authentication | OpenAI published OAuth contract; BCL HTTP, JWT and system credential libraries | [Auth provenance and dependency notices](src/Workbench.Auth/Licenses/PROVENANCE.md) |
| Models and persistence | OpenAI and Anthropic official C# SDKs, Microsoft.Data.Sqlite | [Core dependencies](src/Workbench.Core/DEPENDENCIES.md), [Anthropic integration](src/Workbench.Core/ANTHROPIC.md) |
| Desktop | Avalonia, AvaloniaEdit and declared packages | [Desktop dependencies](src/Workbench.Desktop/DEPENDENCIES.md) |
| MCP | Official ModelContextProtocol.Core 2.3.0, Apache-2.0 | Stdio and Streamable HTTP transports, framing and lifetime; [exact revision and boundaries](src/Workbench.Tools/Mcp/README.md) |
| Browser acceptance | Official Microsoft.Playwright 1.64.1, MIT | Headless Chromium, isolated contexts and assertions; [source revision, browser installation and limits](src/Workbench.Tools/Browser/README.md) |
| Semantic navigation | Microsoft.CodeAnalysis.CSharp 5.9.0, MIT | Source-only symbol binding, definitions/references/diagnostics; [supported scope](src/Workbench.Tools/Navigation/README.md) |
| Benchmarks (development only) | BenchmarkDotNet 0.15.8, MIT | Warmup, measurements and GC/allocation reporting; [runtime/toolchain notes](benchmarks/Workbench.Benchmarks/README.md) |

Exact transitive package versions and content hashes are in each project's `packages.lock.json`; package license metadata remains owned by upstream. Publishing retains the existing per-module `Licenses` files and adds the root license and notices. The self-contained runtime retains its upstream runtime notices as well.

The research modules implement selected mechanisms from ACON, ACE/APC, RepoMem/DCI, GEPA/SkillOpt and Speculative Actions. They do not incorporate the papers' model weights, datasets or benchmark tasks. See [implementation mapping and limits](docs/research-implementation.md), with primary paper links in each module README. Attribution to a paper is separate from licensing copied source; any future source adaptation must record its own original license, fixed revision, changed files and adaptation scope.
