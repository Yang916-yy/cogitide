**English** | [简体中文](model-core-validation.zh-CN.md)

# Model adapters and source evidence acceptance

This is the **2026-10-30 historical model layer snapshot**, including its failures. Later [continuity acceptance](continuity-validation.md) supersedes its then unimplemented summary/rule/skill features; its counts do not describe today's integrated tree.

DeepSeek used official OpenAI .NET 2.14 with none/low/high/max reasoning, full assistant reasoning replay, bound opaque state and cache/reasoning usage. Anthropic 13.3 supplied Messages/thinking/signatures/redacted blocks/images/tool errors through official HTTP/SSE aggregation. Cancellation, timeouts and broken/incomplete streams never committed partial calls or continuation and were not implicitly retried. The only focused TS behavior port was Pi's missing reasoning backfill, with fixed source/MIT in Core dependencies; BCL/SDKs/SQLite/Avalonia supplied infrastructure.

PNG/JPEG/GIF/WebP attachments were bounded to four, four MiB each/eight MiB per request. Whole rounds preserved tool pairing while accounting text, images and full state. Header/MIME checks were not full pixel/dimension decoding; draft UI displayed removable filenames rather than thumbnails. Native provider settings, reasoning choices, opt in compatible endpoint images and bilingual UI were checked. Usage breakdowns were not double counted.

EvidenceMemory projected immutable source events/argument/result hashes, distinguishing successful exit, failure, timeout/error and unknown. Writes/commands/new tasks invalidated old verification, and idle derived views were stale. `inspect_evidence` / `session.evidence` reread evidence without changing/replaying history. Hashes establish consistency, not truth/task success; underlying event reads remained unpaged.

The historical strict build had zero warnings/errors, locked restore passed, and **190 tests passed/one explicit local Codex integration skipped**. Preview packages contained **310/306/306 files**; hashes/architecture/six production locks/licenses passed. Windows packaged Host ran, Linux/macOS did not. Real local Host completed five tools/two approvals/three addition assertions with original file and restart checks. Seven lifecycle categories and actual Desktop Headless assemblies were checked; some earlier Windows settings interactions were performed, while the complete attachment picker flow was not separately accepted.

**The real mixed DeepSeek task failed overall:** code repair/three assertions passed, but a red 128×128 PNG was described as white. Original history and failure remain intact. Separate later requests returned `Red` with thinking none and low, and cold continuation worked without tools; low reported thirteen reasoning tokens. Correct isolated vision does not prove stable mixed long task vision or identify which layer caused the error. Empty reasoning can be valid and must be replayed as an empty string. Claude had no configured live key and only official SDK/offline persistence fixtures passed.

At that stage there was no automatic summary, hierarchical rules, product skill runtime or cross task learning; these were next steps, not current omissions after later work. No performance/research advantage was claimed.

This English page summarizes the preserved source. [The full Chinese record](model-core-validation.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](../src/Workbench.Core/DEPENDENCIES.md)
- [Original reference 2](../research/core-memory-design.md)
