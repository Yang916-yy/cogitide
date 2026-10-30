# Reproducible local performance baseline

This project measures the real workspace read, literal/regex search, Roslyn outline and semantic-reference tools with **BenchmarkDotNet 0.15.8**, warmup/measurement and MemoryDiagnoser. Its external SDK validator does not recognize .NET 11 RC, so this project explicitly uses BDN's mature **in-process emit toolchain** on the actual pinned .NET 11 runtime. This reduces process isolation; retain the reported noise/error bars and repeat on a quiet machine. The fixture has 128 text files (120 lines each) and a source-only C# scope with 30 callers. It is generated locally; no personal repository or external model is used.

```powershell
./.tools/dotnet/dotnet run --project benchmarks/Workbench.Benchmarks -c Release -- --filter '*ToolBenchmarks*' --artifacts artifacts/benchmarks
./.tools/dotnet/dotnet run --project benchmarks/Workbench.Benchmarks -c Release -- --host-startup ./.tools/dotnet/dotnet.exe ./src/Workbench.Host/bin/Release/net11.0/Workbench.Host.dll ./artifacts/benchmarks/host-startup.json
```

Use the pinned SDK, Release builds and an otherwise idle machine. Run SDK commands serially. The short job is a first baseline, not a release performance guarantee; retain distributions/error bars, allocations, runtime, CPU and OS metadata. Async memory accounting has tool/runtime limits. Semantic resolution answers a different question from textual search, so their timings are not interchangeable quality comparisons.

The startup probe uses BCL Process/Stopwatch to launch ten fresh Host processes, await the real initialize reply, sample working set/private bytes/CPU, then close stdin and require a clean exit. Later runs share OS file caches. No model, browser or MCP server calls are included; it does not measure end-to-end coding success or prove C# faster than another language.

**Do not reinvent unnecessary infrastructure.** Use BenchmarkDotNet for benchmarking and .NET diagnostic tools for profiling. The small startup probe measures an application protocol boundary that a method microbenchmark cannot represent.
