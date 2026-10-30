# Multi-file patch provenance

Implemented 2026-10-30. `PatchTools.cs` is a focused C# port of the Codex-style patch module in **OpenCode v1.19.5**, fixed commit [`810c0eba1a7348036f8bdf61e6632d0c36c6876c`](https://github.com/anomalyco/opencode/tree/810c0eba1a7348036f8bdf61e6632d0c36c6876c).

Source: [`packages/opencode/src/patch/index.ts`](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/patch/index.ts). Its MIT notice is retained verbatim in `OpenCode-LICENSE.txt`.

Adapted mechanisms:

- `Hunk` / `UpdateFileChunk`, `parsePatchHeader`, `parseUpdateFileChunks`, and `parsePatch`: `*** Begin Patch`, Add/Update/Delete File, optional Move to, `@@` context anchors, context/removal/addition lines, and End of File.
- `computeReplacements`: match ordered old-line blocks against the original file, advance past prior hunks, support context anchors and append-only hunks.
- `applyReplacements`: apply replacements in reverse order so earlier line positions remain valid.
- `deriveNewContentsFromChunks`: preserve UTF-8 BOM when deriving replacement bytes.

The current Codex grammar was also checked as a format reference in [`codex-rs/apply-patch/src/parser.rs`](https://github.com/openai/codex/blob/788b9d7bf6e37f735647b7f84b556c2a807df4d8/codex-rs/apply-patch/src/parser.rs). The implementation above is adapted from the MIT-licensed OpenCode module; no Codex runtime, process, parser package, or source module is incorporated.

## Deliberate C# integration changes

- Uses BCL byte I/O, SHA256, async cancellation, file modes, atomic single-path replacement and the existing workspace resolver/shared edit locks. It needs no Git repository and does not implement a general diff generator.
- Strict, unique, exact context matching replaces upstream first-match/whitespace/Unicode-punctuation fallback. Ambiguous blocks require more context. EOF anchors must match the actual end. Unknown syntax and shell/heredoc wrappers are rejected rather than skipped.
- All paths are relative, bounded, distinct, non-overlapping and checked for traversal, Git internals, invalid/device names, symbolic links and junctions (including dangling links). Pure renames are allowed; destination overwrite and case-only rename aliases on Windows are rejected.
- Existing Update/Delete/Move sources require the SHA256 obtained by reading them. Hash-map entries must match precisely those source paths. Add and Move targets must be absent. Scoped project-rule discovery uses the same full parser and returns both move source and destination.
- All files/hunks are validated before staging. Original backups and replacement files are staged before any target commit. Targets are rechecked before the commit phase and immediately before each replacement. Controlled commit failure or cancellation triggers reverse rollback; rollback refuses to overwrite an external edit and retains affected backups with explicit recovery paths when restoration is incomplete.
- This is **not a power-loss-atomic multi-file transaction or a filesystem sandbox**. A process/system crash during commit may leave a partial patch and staged recovery files. External filesystem mutations cannot be made atomic with ordinary path APIs. No success is reported for a controlled partial failure.
- Existing UTF-8 BOM, per-line CRLF/LF/CR endings, and final-newline presence are preserved. Unchanged mixed endings remain intact. New lines use local replaced-line endings or the file's first existing ending; Add files use UTF-8 without BOM and LF with a final newline for nonempty content. Pure moves preserve exact bytes. Binary/UTF-16 files are rejected.
- Bounds: 64 source operations, 512 hunks, 100,000 patch lines, 2 million patch characters/context comparisons, 1 MiB per file, 8 MiB combined original/replacement bytes, and 80,000 characters of result metadata (validated before writing so the engine need not truncate JSON evidence).

`PatchTests` covers real filesystem multi-file application, hash conflicts, late invalid hunks, malformed/ambiguous patches, Unicode/BOM/mixed endings, moves and deletion, EOF anchors, traversal and link rejection, cancellation, output limits and Windows sharing-violation rollback. Unix mode checks execute on Unix; cross-platform runtime validation still requires those hosts.
