# MCP, browser acceptance, semantic navigation and measured performance

Date: **2026-10-30**. These results describe source builds after the initial publication. English is now the default Markdown language; 37 substantial Chinese records remain in adjacent `*.zh-CN.md` files, with bidirectional language links. English historical summaries identify the full Chinese records rather than claiming synchronized word-for-word translations.

## CI diagnosis and correction

The initial macOS job failed 13 tests. Twelve were skill-optimization tests rejecting a legitimate macOS system temporary path because `/var` is an OS-owned alias for `/private/var`; one cancellation test's timeout was a consequence of initialization failing. The patch metadata fixture also constructed a UTF-8 path beyond macOS `PATH_MAX` before it could reach its intended encoded JSON limit.

The optimizer now resolves only the standard macOS `/var` and `/tmp` aliases when their actual BCL-resolved targets are exactly `/private/var` and `/private/tmp`. All user-defined links below them remain rejected. A new regression checks user links. The patch fixture uses a shorter path while its 64 escaped entries still exceed the 80,000-character metadata limit. The targeted Windows validation passed **59 tests**. Commit [`fccfd59`](https://github.com/Yang916-yy/cogitide/commit/fccfd5964808bf71bb25957e635e42e2ff651330) then passed [all three GitHub CI jobs](https://github.com/Yang916-yy/cogitide/actions/runs/41047341833): Windows, Linux and macOS. This establishes headless build/test behavior, not native desktop certification.

## Integrated capabilities

| Capability | Actual implementation | Boundaries |
| --- | --- | --- |
| MCP | Official ModelContextProtocol.Core **2.3.0**; stdio and Streamable HTTP; configured tools use the same AgentEngine and approval flow | Explicit Host data-directory `mcp.json`; up to 64 tools; fresh owned clients per run; ReadOnly never connects. Configured environment/header variable names only, provider secrets excluded. No private OAuth simulation. |
| Browser acceptance | Official Microsoft.Playwright **1.64.1**, fresh headless Chromium context, text/CSS/title/status assertions, console/page/network evidence and PNG screenshots | Explicit origins; no inherited profile; HTTP redirects blocked and reported. This is not an OS sandbox or hard network-response RAM limit. Browsers must be installed separately. |
| Semantic code navigation | Existing Roslyn **5.9.0**; `csharp_semantic` definitions, references and compiler diagnostics, with exact symbol/overload identity and UTF-16 positions | Bounded source-only compilation in the nearest project scope. No MSBuild execution, generators, package/project-reference loading or full LSP server. Snapshot hashes protect pagination. |

The Host exposes **19 built-in/knowledge tools**, or **13** in ReadOnly. Optional repository history adds one; configured MCP tools are discovered per run and are classified as approval-required. Explicit full-access/auto-approval grants follow the same policy as other tools. Semantic navigation is read-only; browser checks require approval. External clients are disposed before a terminal status is committed. A cleanup failure emits an explicit event and marks the run failed; it cannot leave a successful completion record.

.NET text search (`Regex`, globbing and string matching) finds character patterns. Roslyn's semantic model binds a particular token to a type/member/local and resolves overload identity. These complement each other; a textual match is not a semantic reference. The [Microsoft semantic-model documentation](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-semantics) explains the distinction.

## Functional verification

- Strict Debug and Release builds: zero warnings/errors.
- Full regression suite: **553 passed, 0 failed, 0 skipped**, including real SDK transport and headless browser fixtures, 11 semantic cases and 16 additional-tool lifetime/approval cases.
- Existing Host, Responses, lifecycle, continuity, tools and research fixtures exercise local SSE models and generated repositories. Exact tool-count expectations were updated for the two new tools.
- `python scripts/verify-integrations.py` passed through the actual stdio Host and local SSE model: denied MCP call did not execute; a later approved Unicode echo did; ambient/provider credentials were absent; browser assertions produced a valid PNG; Roslyn found the correct cross-file overload and reference; a cold restart preserved events; ReadOnly neither reconnected nor replayed actions. Both owned MCP workers exited. Eight local model requests, no paid or external model calls.

The fixture reuses existing Host RPC/process helpers and records assembly hashes in local `artifacts/verification/integrations/<id>/result.json`. CI runs the same script after installing matching headless Chromium. Existing research behavior is verified independently; none of these fixtures proves coding-task superiority.

## Performance baseline

Machine: Windows 11 build 26300.9711, Intel Core i7-13700H (20 logical / 14 physical cores), .NET SDK `11.0.100-rc.3.26522.148`, Release/x64 RyuJIT. BenchmarkDotNet **0.15.8**, one launch, three warmups and ten measurement iterations. Its external SDK validator does not recognize the .NET 11 RC runtime moniker. We therefore use its supported **in-process emit toolchain on the actual .NET 11 runtime**; this trades process isolation for preview-runtime compatibility. The initial unsupported-toolchain attempt is retained in local logs and is not counted as a successful run.

The filesystem fixture is not a Git repository: 128 text files with 120 lines each. The semantic fixture has 31 C# sources and 30 callers. Each tool executes its real path/hash/inventory/budget logic. Semantic queries rebuild their source compilation; there is no index cache. Results use warm OS file caches. Async allocation numbers are managed allocations reported by BDN, not peak process memory or total native allocation.

| Operation | Mean | StdDev | Managed allocation / operation |
| --- | ---: | ---: | ---: |
| File page, 20 lines | 0.691 ms | 0.028 ms | 68.2 KiB |
| Literal search, 128 text files | 179.964 ms | 13.717 ms | 8.08 MiB |
| Regex search, 128 text files | 201.311 ms | 10.630 ms | 8.10 MiB |
| Single-file syntax outline | 0.654 ms | 0.024 ms | 19.3 KiB |
| Semantic references, 31 C# files | 253.665 ms | 16.832 ms | 7.03 MiB |

BDN removed one outline outlier, leaving nine reported measurement values for that method. The [machine-readable baseline](performance-baseline.json) includes medians, p95, 99.9% confidence margins, allocations, method sample counts and the raw report SHA-256. One launch and a laptop environment limit generalization; no CI latency threshold is inferred.

A separate BCL Process/Stopwatch probe launched ten fresh Host processes, waited for the real `initialize` reply, sampled process memory/CPU and required clean exit after stdin closed. Median initialization was **329.3 ms**; range **196.8–1487.3 ms**, with the first sample **1487.3 ms**. Average working set was **41.2 MiB**, private bytes **11.8 MiB**. Later processes share OS caches, so these are fresh-process measurements rather than forced cold-boot measurements. No model, MCP connection or browser launch was included.

These data identify repository search allocation and source-compilation rebuilding as useful profiling targets. They do not establish that C# is faster than TypeScript/Rust, that the agent outperforms Codex/OpenCode, or that research features reduce real task cost. Real task/model latency, browser/MCP performance, Linux/macOS timings and before/after optimization remain unmeasured.

A subsequent [allocation-tuning report](performance-tuning.md) provides its own matched before/after sources, reverse-order probes, native traces and rejected cache experiments. The measurements above remain the original historical baseline.

Reproduce with the commands in [the benchmark project](../benchmarks/Workbench.Benchmarks/README.md), using an idle machine and serial SDK operations. Raw BDN JSON/CSV/HTML, startup samples and logs remain in ignored `artifacts/`; the portable numeric summary is committed here. .NET counters/traces can be used next if profiling a measured bottleneck warrants it.

## Reuse and licensing

No new HTTP/SSE stack, browser driver, MCP parser, regex engine or C# parser was handwritten. Official SDKs own their transports and runtime details; adapters reuse the common agent protocol and .NET cancellation, JSON, hashing and process components. Exact SDK revisions and retained upstream notices are indexed in [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md). **Do not reinvent unnecessary infrastructure.** Any future custom implementation must document the actual gap and validation evidence.

## Follow-up CI compatibility checks

The first integration CI run passed Windows/Linux and exposed further macOS portability defects: Python reports physical `/private/var` cwd aliases, and the pinned runtime's tree-kill regression can hang child process cleanup. The cwd test now verifies random file identity through both paths instead of comparing their spelling. macOS commands and MCP use a shared, bounded system-process snapshot/BCL single-PID kill workaround, while the MCP wire protocol stays entirely in the official SDK's public StreamClientTransport.

A Linux run also exposed the small interval between a durable terminal event and Host ownership release. A new run now checks one indexed journal tail record and joins the finishing task before acquiring the next run; active tasks are still rejected. CI setup, browser installation and tests are separate timed steps, with VSTest hang diagnostics. These follow-ups do not change the measured Windows tool operations.
