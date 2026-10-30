**English** | [简体中文](README.zh-CN.md)

# Revocable read only prefetch

The mechanism follows [Speculative Actions v2](https://arxiv.org/html/2510.04371v2): predict a future action, overlap it with model inference, and consume only if the actual action matches. A domain heuristic chooses the first file from `search_files`/`glob_files` and predicts default `read_file` pagination. No author code or predictor model call is copied/added; writes, commands and general multi branch speculation are excluded.

`ReadOnlyPrefetch.cs` reuses `IAgentTool`, BCL `Task`, cancellation, the existing path resolver and [bounded shared hashes](../../Memory/EvidenceFileHash.cs). Each run owns one unconsumed branch, default four/max eight prefetches, five seconds each. Results stay outside conversation/evidence until tool name, physical path, page parameters and a current full file SHA-256 match. Unknown parameters, mismatch, changed content or failure fall back to an ordinary read.

Project safeguards are run isolation, cancel and wait on writes/steering/end, no output callback, existing ignore/link boundaries and a 16 MiB + one byte hash read cap. These are engineering additions rather than proved novelty. Only built in cooperative `read_file` is supported, not arbitrary plugin speculation. A file can still change after validation, so edits require normal version protection.

Enable through `ResearchOptions.ReadOnlyPrefetch` or desktop experiments. `speculation.started/hit/summary` records usage/hits, including wasted I/O. Default off has no cross session/account cache. [SpeculationTests](../../../../tests/Workbench.Tests/SpeculationTests.cs) exercises real AgentEngine overlap, matches, fallback, cancellation and lifetime. Real task latency/accuracy gains have not been measured.

**Do not reinvent infrastructure.** Reuse BCL and existing tools rather than a scheduler, filesystem parser, hash algorithm, thread pool or cache service. Expansion needs reversible/side effect boundaries, matching evidence and measured benefit before enabling it.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.
