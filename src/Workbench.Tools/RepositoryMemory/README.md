**English** | [简体中文](README.zh-CN.md)

# Repository memory: frozen revision history localization

`repository_memory` is an optional read only tool. At each run start, the Host freezes one full Git commit ID; all history queries stay inside its reachable ancestry without moving a ref. Missing commits/repositories fail explicitly without unrelated directory scanning or remote fetches. This is neither a full RepoMem/LocAgent reproduction nor DCI-Agent.

The independently written C# implementation borrows episodic localization from [Repository Memory v2](https://arxiv.org/html/2510.01003v2): commit messages, path terms and cochanges with implicated files. It does not implement the paper's LLM functional summaries, learned retriever or LocAgent graph. [DCI](https://arxiv.org/html/2605.05242) motivates direct raw source search and narrowing; the [author's Lite implementation](https://github.com/DCI-Agent/DCI-Agent-Lite) was reviewed but its code, prompts, harness and training infrastructure were not copied. Existing `search_files`, `read_file` and `inspect_evidence` remain the navigation loop.

Project additions freeze ancestry, restrict candidates to normal blobs existing at the base, record source commits/base blob IDs/bounded raw excerpts, separate history relevance from current file validity, and validate pagination snapshots. `seedPaths` can retrieve cochanges even with an empty query; seeds must remain within `pathPrefix`. Scores are deterministic heuristics: path matches four points, commit message matches three (up to 24 terms), cochange commits six (up to eight). They are not probabilities or causal evidence; instructions in history remain untrusted data.

Git CLI and existing `WorkspaceService.ReadGitAsync` provide revision/ancestry/blob/metadata/numstat behavior. Metadata and diff are read separately to prevent forged messages becoming paths. Replace objects, optional locks, external diff and textconv are disabled; argv avoids a shell. BCL `Process`, asynchronous I/O, cancellation, `SemaphoreSlim`, SHA-256, dictionaries and System.Text.Json supply infrastructure. No author research code, model calls, vector store, database or second runtime was imported.

One tool instance caches only its latest immutable history corpus; current file validity is recomputed. No future history, remote objects, Git internals, deleted nonbase paths, symlinks/submodules or currently linked paths become candidates. Binary changes are skipped. Current file contents are not searched or claimed verified by history.

Bounds: default 32/max 64 commits; 128 detailed changes per commit; base inventory 20,000 files/2,000,000 characters; about 4,000,000 aggregate metadata characters; 65,536 characters for a single Git observation; 25 second scan plus shared 20 second Git timeout. Truncation is explicit and requires narrower scope. Pages contain at most 50 candidates, four sources each and 80,000 JSON characters. Excerpts include offsets and original lengths.

`RepositoryMemoryTests` exercises real temporary Git repositories: future fix leakage, stale/foreign bases, cochanges, scope/UTF-8, message forgery, binary/link filtering, stale pages, cancellation and missing repositories. Fixtures do not establish SWE-bench gains or paid model effectiveness.

**Do not reinvent infrastructure.** Reuse existing BCL/Git/path checks. A new retriever/index layer needs a measured missing capability, alternatives, fixed source/license records and scope validation. This engineering combination has no claim of proved academic novelty.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.
