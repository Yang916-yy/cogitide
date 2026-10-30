# Measured .NET tool overhead and cross-domain opportunities

Measured on **2026-10-30**. This report compares committed baseline `9f89241e00ae263df6772d354f52b5efd3da5ff6` with the source hashes in [the numeric record](performance-tuning.json). It preserves unsuccessful experiments. The earlier [integration baseline](integrations-performance.md) is a historical run, not the denominator for these changes.

## Changes adopted

- **Bounded file reads:** BCL `ArrayPool<byte>`, `FileStream.ReadAtLeastAsync`, strict `UTF8Encoding.GetString(ReadOnlySpan<byte>)` and `SHA256.HashData` replace a fresh staging array, growing MemoryStream and a second decoder/StringBuilder path. Read exactly the bounded length, probe EOF to reject growth, validate metadata, hash all original bytes including a BOM, decode once, and clear pooled buffers on return. No private source cache is introduced.
- **Search context:** keep preceding line ranges in a small ring and materialize context strings only for returned matches. Continue scanning/hashing all eligible files and counting all matches; a full result page cannot suppress a later regex timeout. Existing regex/glob/path policies and output caps remain.
- **Inventory and result projection:** use BCL span splitting for Git's NUL-delimited paths and avoid an immediate copy when the complete result list already fits. Git still owns ignore semantics.
- **Roslyn candidates:** ordinary symbols can reject a clearly different `Identifier.ValueText` before binding. Matching spelling is never sufficient; Roslyn still proves symbol identity. Constructors, types/aliases, operators/conversions and tuple aliases retain the full binding path. The selected source's SemanticModel is reused within the current query, with no cross-query compilation cache.

These APIs are mature .NET components, not all new in .NET 11. Measurements run on the pinned .NET 11 RC; they do **not** isolate an upgrade from .NET 10 or compare C# with another language.

## Results and uncertainty

| Operation | Before BDN mean | Final BDN mean | Managed B/op, before → after | Allocation change |
| --- | ---: | ---: | ---: | ---: |
| ReadPage | 0.410 ms | 0.631 ms | 69,727 → 24,996 | -64.2% |
| LiteralSearch | 151.518 ms | 296.157 ms | 8,454,268 → 2,720,664 | -67.8% |
| RegexSearch | 155.973 ms | 172.209 ms | 8,465,524 → 2,728,312 | -67.8% |
| SyntaxOutline | 0.482 ms | 0.412 ms | 19,685 → 19,601 | -0.4% |
| SemanticReferences | 154.001 ms | 146.766 ms | 7,357,661 → 7,360,702 | 0.0% |

Reverse-order diagnostic probes (48 measured calls per variant, not BDN):

| Operation | Baseline mean | Candidate mean | Candidate time change |
| --- | ---: | ---: | ---: |
| read | 0.649 ms | 0.543 ms | -16.3% |
| literal | 169.645 ms | 156.485 ms | -7.8% |
| regex | 146.489 ms | 162.367 ms | +10.8% |
| syntax | 0.468 ms | 0.521 ms | +11.4% |
| semantic | 200.745 ms | 183.908 ms | -8.4% |

The final BDN literal-search mean spiked to 296 ms (99.9% confidence margin ±107 ms). A follow-up with the **final** source ordered candidate/baseline/baseline/candidate measured 181.4/168.4/171.2/166.7 ms respectively: combined candidate 174.0 ms versus baseline 169.8 ms (+2.5%), with the same allocation reduction. All runs remain recorded; this does not erase the slower BDN run or establish a speedup. Shared-machine variation and source effects are not separately identified.

The strongest repeatable result is lower **managed allocation** for reads and search. It is not a claim of lower retained memory: ArrayPool may retain buffers, Roslyn/native memory is outside managed allocation accounting, and clearing rented buffers costs CPU. A 16 MiB per-file read cap does not make the whole process a 16 MiB process.

Elapsed timings varied substantially on this shared laptop, including the unchanged syntax-outline control. An early candidate measured 25.5% lower literal-search time, while a later complete BDN run was slower for every operation. The reverse-order checks are reported rather than selecting the fastest run. No general latency improvement or CI latency threshold is asserted. Model/network latency, task success, billable tokens and end-to-end agent throughput were not measured.

## Rejected: shared runtime metadata cache

Two isolated candidates reused Roslyn references backed by immutable images of public runtime assemblies. Each request fully hashed the current public runtime catalog; it did not cache private source or trust size/mtime. The first asynchronous validator reduced allocations but changed semantic mean from **154.0 ms to 272.4 ms**. Moving that bounded validation into one synchronous BCL read loop reduced the regression to **187.2 ms**, still **21.5% slower** than that baseline, despite allocations falling from **7.36 MB to 2.65 MB** (decimal).

Both implementations, their cache-specific production identity contract, and their cache tests were withdrawn. Repeated full-catalog validation and retaining assembly images are real costs. Replacing hashes with timestamps merely to obtain a favorable number would change the evidence contract. This negative result does not establish that every metadata cache is bad; it rejects these candidates for this workload. Raw experimental report hashes are retained in the numeric record.

## .NET 11-specific review

The official [.NET 11 performance review](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/) describes JIT improvements for safe span loops and an opt-in `runtime-async=on` lowering. Normal bounded BCL/span code can benefit without custom SIMD, unsafe pointer loops, another scheduler, or replacing public Task APIs with ValueTask everywhere. Those runtime changes are not separately attributed by this experiment.

An isolated worktree built the same optimized source with `Features=runtime-async=on`. PEReader confirmed the feature was effective: Workbench.Tools changed from **443,392 B / 0 runtime-async methods** to **373,248 B / 58 runtime-async methods** (15.82% smaller module). This is one assembly, not installer size, process memory or end-to-end performance. The numeric record retains this experimental BDN run. Additional managed-allocation reduction was small for these tools and timing varied alongside the unchanged outline control. **Runtime Async remains disabled in the main build** pending broader workload/cold-start/three-platform lifecycle evidence; the isolated result does not justify a default change.

Existing transport already reuses `SocketsHttpHandler` and official model SDKs; wire/state JSON uses source-generated System.Text.Json; fixed tool catalogs use FrozenDictionary; fixed patterns use GeneratedRegex. These should remain the defaults. User-supplied regex patterns are dynamic and cannot simply become GeneratedRegex. New .NET 11 process-output convenience APIs do not alone replace bounded dual-stream draining, cancellation and owned-process cleanup. No speculative HTTP/SSE/parser rewrite, global source cache, or desktop-wide AOT switch was made.

## Diagnostics and reproducibility

Machine: Windows 11 build 26300.9711, Intel Core i7-13700H, 20 logical / 14 physical cores, Release/x64, SDK **11.0.100-rc.3.26522.148**. Fixture: 128 UTF-8 text files × 120 lines; semantic scope: 31 C# sources / 30 callers, no package or project loading. Each tool uses the normal path/hash/inventory budgets. The current diagnostic harness adds a semantic-series command and a default-true setup guard; the generated fixture, measured methods and BDN job remain the same. Both harness hashes are retained. These are generated local files, not personal code.

BenchmarkDotNet **0.15.8** uses InProcessEmitToolchain because its external SDK validator does not recognize this preview moniker. One launch, three warmups and ten measurement iterations; outlier filtering leaves the recorded per-method sample counts. BDN iteration percentiles are not production request tail latency. Reverse-order probes use fresh processes, three warmups, 24 calls each, ordered baseline/candidate/candidate/baseline per operation. OS caches are not forcibly cleared. Process-wide allocation probes include runtime activity.

Native profiling uses official **dotnet-trace 10.0.750501** with `dotnet-sampled-thread-time,gc-verbose`. Sampled thread-time includes waiting and idle threads; its percentages are **not CPU utilization**. GC allocation ticks identify sampled allocation pressure, not exact bytes attributable to each type. The after trace supports removing staging/decoder allocations; it is not the performance score. TraceEvent (already a BenchmarkDotNet dependency) parses the trace. No custom profiler was written. Raw traces, BDN exports and logs remain under ignored `artifacts/performance-tuning/`; the portable numeric summary records their SHA-256 hashes.

See [benchmark commands](../benchmarks/Workbench.Benchmarks/README.md). SDK/build/test/benchmark operations were serialized and no desktop app was opened.

## Tool innovation and paper transfer

The [primary-source review](../research/cross-domain-agent-optimization.md) maps database query planning, PLDI demand-driven incremental computation, operating-system scheduling/value of computation, transactional-history witnesses, ICML LLMCompiler and repeated tool reliability to current code. It also includes the 2026 *The Devil Is in the Interface* preprint. Paper results are evidence within their own experiments, not measured Cogitide gains or conference oral/spotlight claims.

Two concrete tool extensions remain **designs, not added production tools**:

1. **Views over persisted evidence:** extend existing event-sequence evidence recall to retrieve a later result page or selected source lines without reexecuting the original tool. Reuse the event store and source hashes; explicitly label historical evidence. Test recovered useful spans per returned character against current evidence recall, context snippets and ordinary reads.
2. **Differential evidence recheck:** rehash a bounded set of existing file observations and report unchanged/changed/missing/unreadable/policy-changed/unverified. Reuse existing path rules, evidence hashes and experience validation; do not create a second cache/store. It can reduce returned context, but revalidation still reads complete files. It cannot refresh an old command's success or an entire compilation from one file hash.

The comparison must include current search context and multiple calls in one assistant response. A generic batch wrapper is not automatically a new capability or a saved provider turn. Test these extensions independently on deterministic histories before any explicitly budgeted live-model evaluation. Learned scheduling, a general query engine, another plan language and a persistent private-source graph remain deferred.

## Verification

Release `scripts/build.ps1` completed locked restore, a warning-as-error build with zero warnings, Chromium setup and **569/569 passing tests**. Sixteen new regression cases cover byte/UTF-8/hash/context invariants and Roslyn identifiers/constructor/operator/nameof behavior. The suite exposed two issues: the pre-existing unique `nameof(method)` group was omitted, now resolved only through compiler-confirmed nameof and a single Roslyn member; a browser cancellation fixture could close a response after its listener queue was disposed, now tolerated only during fixture shutdown. Overloaded nameof groups remain unresolved rather than arbitrarily selecting a method.

The Debug warning-as-error build also passed. All seven local Host fixtures passed: `verify-host`, `verify-lifecycle`, `verify-responses`, `verify-continuity`, `verify-tools-upgrade`, `verify-research`, and `verify-integrations`. They use local generated repositories, local model/protocol fixtures and headless Chromium; no desktop session or paid model was used. Module inventory, relative Markdown links and whitespace checks passed. CI checks correctness across operating systems; it is not this Windows performance experiment.

## 中文入口

本轮落实的是 .NET 读取与搜索分配优化、严格的分页/哈希/取消回归，以及可复核的对照与原生诊断。耗时受共享机器负载干扰，不宣称稳定提速；完整验证的元数据缓存因耗时回退已撤回。跨领域论文对应的证据投影视图、差异复核是待验证设计，尚未增加生产工具或付费模型评测。
