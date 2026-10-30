**English** | [简体中文](README.zh-CN.md)

# Shared protocol and data contracts

`Contracts.cs` defines versioned JSONL requests, notifications, sessions, messages, tools, run options, account/model status and interfaces. Research behavior requires explicit `ResearchOptions`. `CompactionContracts.cs` defines whole tool group boundaries, source summaries and versioned checkpoints. `WireJsonContext.cs` supplies generated `System.Text.Json` metadata for stable wire/storage DTOs; `ImageInputs.cs` validates supported signatures, base64 and image byte budgets.

These are Cogitide's own interprocess contracts, inspired by established Host/client separation. They are not a complete Codex or OpenCode protocol port. JSON, base64, serialization and hashing reuse .NET/BCL, with the necessary compatibility fallback for dynamic payloads.

Provider continuation remains opaque and bound to its endpoint/model. It is neither an account token nor portable state for an unrelated provider. Schema changes require compatibility or an explicit version increment; unknown results never become successes. Research defaults off and does not authorize installation or training.

Optional additive `RunCheckpoint`, `RunUsage` and `RunOptions.MaxReportedTokens` fields support explicit durable task continuation. `RunLimits.Validate` is the shared entry-point validator; default runs allow 256 iterations/six hours, with bounded maxima of 10,000 iterations/seven days. Reported token usage is not an exact cost estimate; missing provider usage stays visible. `SessionView`, `SessionMessagePage`, `SessionEventPage` and `PendingInputPage` expose bounded presentation/recovery projections with original source cursors, totals and explicit unavailability/omission metadata. Image descriptors in display-only pages deliberately contain empty base64 data and must never be used as model inputs. Production SQLite overrides default `ISessionStore` compatibility implementations to avoid loading the full source snapshot or journal for these pages.

**Do not reinvent infrastructure.** Extend these shared DTOs instead of duplicating them in GUI, Host or research CLI. Reuse System.Text.Json and BCL encoding; a replacement serializer needs a demonstrated limitation, migration plan and compatibility/boundary tests.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](Contracts.cs)
- [Original reference 2](CompactionContracts.cs)
- [Original reference 3](WireJsonContext.cs)
- [Original reference 4](ImageInputs.cs)
