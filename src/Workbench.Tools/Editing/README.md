**English** | [简体中文](README.zh-CN.md)

# Multi file patches

`PatchTools.cs` extends `WorkspaceService` with `apply_patch` Add/Update/Delete/Move operations, ordered context hunks and end of file placement. All source paths, move destinations, hashes and output budgets are checked before commit. The same parser supplies path information to project rules.

The format and ordered hunk/replacement mechanism are a focused C# adaptation of **OpenCode v1.19.5**, commit `810c0eba1a7348036f8bdf61e6632d0c36c6876c`, `packages/opencode/src/patch/index.ts`. [Patch provenance](../Licenses/Patch/PROVENANCE.md) records the exact scope, differences and original MIT license. Existing files require current SHA-256; contexts must match exactly and uniquely. Loose whitespace or punctuation guesses are deliberately excluded.

Paths, byte encoding, hashes, cancellation and temporary files reuse BCL and [WorkspaceService](../Workspace/README.md). BOM, line endings and applicable permissions are preserved. Controlled commit failures attempt rollback and retain explicit recovery files if safe restoration fails. A multi file patch is not a power loss or crash atomic transaction.

**Do not reinvent infrastructure.** Reuse this parser/edit boundary or mature standard tools. Changes need upstream/version/license records, ambiguity tests and rollback/scope evidence; convenient fuzzy matching must not bypass version checks.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](PatchTools.cs)
