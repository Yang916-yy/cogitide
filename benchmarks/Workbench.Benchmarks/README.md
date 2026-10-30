# Reproducible local performance baseline

This project measures the real workspace read, literal/regex search, Roslyn outline and semantic-reference tools with **BenchmarkDotNet 0.15.8**, warmup/measurement and MemoryDiagnoser. Its external SDK validator does not recognize .NET 11 RC, so this project explicitly uses BDN's mature **in-process emit toolchain** on the actual pinned .NET 11 runtime. This reduces process isolation; retain the reported noise/error bars and repeat on a quiet machine. The fixture has 128 text files (120 lines each) and a source-only C# scope with 30 callers. It is generated locally; no personal repository or external model is used.

```powershell
./.tools/dotnet/dotnet run --project benchmarks/Workbench.Benchmarks -c Release -- --filter '*ToolBenchmarks*' --artifacts artifacts/benchmarks
./.tools/dotnet/dotnet run --project benchmarks/Workbench.Benchmarks -c Release -- --host-startup ./.tools/dotnet/dotnet.exe ./src/Workbench.Host/bin/Release/net11.0/Workbench.Host.dll ./artifacts/benchmarks/host-startup.json
```

Use the pinned SDK, Release builds and an otherwise idle machine. Run SDK commands serially. The short job is a first baseline, not a release performance guarantee; retain distributions/error bars, allocations, runtime, CPU and OS metadata. Async memory accounting has tool/runtime limits. Semantic resolution answers a different question from textual search, so their timings are not interchangeable quality comparisons.

The startup probe uses BCL Process/Stopwatch to launch ten fresh Host processes, await the real initialize reply, sample working set/private bytes/CPU, then close stdin and require a clean exit. Later runs share OS file caches. No model, browser or MCP server calls are included; it does not measure end-to-end coding success or prove C# faster than another language.

**Do not reinvent unnecessary infrastructure.** Use BenchmarkDotNet for benchmarking and .NET diagnostic tools for profiling. The small startup probe measures an application protocol boundary that a method microbenchmark cannot represent.

## Before/after diagnostics

[The tuning report](../../docs/performance-tuning.md) includes controlled source manifests, reverse-order checks and rejected experiments. Preserve the complete comparison; do not select the fastest iteration or compare a new run against an unrelated historical baseline.

```powershell
New-Item -ItemType Directory -Force artifacts/performance-tuning | Out-Null
$benchDll = './benchmarks/Workbench.Benchmarks/bin/Release/net11.0/Workbench.Benchmarks.dll'
./.tools/dotnet/dotnet.exe $benchDll --workload literal 24 artifacts/performance-tuning/literal.json
./.tools/dotnet/dotnet.exe $benchDll --semantic-series 8 artifacts/performance-tuning/semantic-series.json
```

`--workload` accepts `read`, `literal`, `regex`, `syntax`, or `semantic`: generated identical fixtures, three warmups, 1..500 real calls, process-wide managed allocations, CPU time and peak working set. It is a diagnostic probe, not BenchmarkDotNet or an agent-quality benchmark. `--semantic-series` measures a first semantic call and repeats in one fresh process without warming semantic binding; it does not clear OS caches.

Install the official diagnostics tool privately if needed (the measured version was 10.0.750501), then profile a bounded workload:

```powershell
./.tools/dotnet/dotnet.exe tool install dotnet-trace --version 10.0.750501 --tool-path .tools/diagnostics --allow-roll-forward
$env:DOTNET_ROOT = (Resolve-Path .tools/dotnet).Path
./.tools/diagnostics/dotnet-trace.exe collect --profile dotnet-sampled-thread-time,gc-verbose --buffersize 64 --format Speedscope --output artifacts/performance-tuning/literal.nettrace -- ./.tools/dotnet/dotnet.exe $benchDll --workload literal 60 artifacts/performance-tuning/literal-traced.json
./.tools/dotnet/dotnet.exe $benchDll --allocation-summary artifacts/performance-tuning/literal.nettrace artifacts/performance-tuning/allocation-types.json
```

Thread-time sampling includes waiting/idle time, not CPU percentages. Allocation ticks include startup/warmup and are sampled, not exact bytes per type. The parser reuses TraceEvent already supplied by BenchmarkDotNet. Tracing perturbs workload time; use untraced matched runs for performance comparisons. Keep SDK/build/test commands serial and never overwrite binaries while a comparison is running. Runtime Async experiments must use the same optimized source in a separate worktree; changing both source and compiler lowering does not isolate the compiler feature.
