# Conversation compaction provenance

Reviewed and adapted on 2026-10-30. This directory's license files are included in build and publish output.

## Pi

- Repository: <https://github.com/earendil-works/pi>
- Fixed commit: `e7a0df27c09d11bf4161d3605246cbf57a129cb2`
- [compaction.ts](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/coding-agent/src/core/compaction/compaction.ts): `findCutPoint`, structured summary headings and incremental summary update instructions, completed summary validation and usage accounting.
- [utils.ts](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/coding-agent/src/core/compaction/utils.ts): separation of the summarization task from the serialized historical transcript.
- [compaction documentation](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/coding-agent/docs/compaction.md).
- MIT, copyright (c) 2025 Mario Zechner; exact notice retained in `Pi-LICENSE.txt`.

`ConversationCompaction.FindCutPoint` translates the backwards accumulation over valid boundaries into C# over `ChatMessage`, selecting complete assistant/tool batches. `SummaryInstructions` adapts the six-section checkpoint template and update behavior. There is no copied Pi session tree, plugin hook system, token estimator or retry layer. The recent suffix uses the application's character budget; it is not an exact provider token estimate.

## OpenCode

- Repository: <https://github.com/anomalyco/opencode>
- Fixed commit: `60072747957926e7aeec7e911fe6e23abe9bf830`
- [compaction.ts](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/session/compaction.ts): 15% reserve before the context ceiling; progressive clipping of old observations; summary update instructions that prefer newer history, drop resolved work, and exclude repository instruction files that are loaded separately.
- MIT, copyright (c) 2025 opencode; exact notice retained in `OpenCode-LICENSE.txt`.

`ConversationCompaction.Prepare`, `Transcript` and `SummaryInstructions` adapt these focused parts. Cogito does not port Effect services, background precomputation, adaptive provider-overflow retry, or OpenCode's token window constants. It makes at most one failing summary attempt per run; a later user-initiated run may try again.

## Cogito integration and deliberate differences

`AgentEngine` commits a completed checkpoint in the existing SQLite session snapshot and emits usage separately. The full original conversation and event journal remain durable. A SHA-256 over the covered original messages validates that a checkpoint belongs to the current history after restart. This hash establishes source consistency, not the truth of the model's summary.

Original user text is pinned verbatim alongside the summary and recent suffix. Active-task attachments remain governed by the existing image budget. Private provider continuation state is never sent to the summarizer. Summary requests expose no tools, are size bounded after JSON encoding, and cannot commit after cancellation or a failed/incomplete stream. A context that cannot retain original instructions fails explicitly. Project rules and the skill catalog are reloaded outside the checkpoint, including after a slow summary; a changed rule during tool approval forces a new model proposal.

These integration safeguards and source checks are project engineering, not claims of reproducing a research paper. There is no automatic replay of tool actions or unbounded automatic continuation past user-set iteration/time limits. .NET records, `IAsyncEnumerable`, `CancellationToken`, `System.Text.Json`, `IncrementalHash` and the existing SQLite transaction implementation supply the runtime machinery.

The focused ports are in `ConversationCompaction.cs` and their orchestration in `AgentEngine.cs`; checkpoint storage contracts are in `Workbench.Protocol/CompactionContracts.cs`. `CompactionTests.cs` covers repeated summaries, SQLite cold resume, whole tool groups, pinned constraints, summary cancellation/failure, steering, source invalidation, encoded request bounds and dynamic project-rule changes.
