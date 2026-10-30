**English** | [简体中文](implementation-status.zh-CN.md)

# Engineering status and acceptance history

This is a faithful English summary of the **2026-10-30 engineering snapshot**. Its historical internal name Cogito was later replaced publicly by Cogitide; Workbench technical names/data paths stayed compatible. Counts and capability limits describe that round, with later work reported separately. Original successes, failures and unimplemented items remain in the Chinese record.

The architecture already used one AgentEngine for official ChatGPT authentication, Responses API keys, Chat Completions, DeepSeek and Claude. Protocol owned versioned JSONL/contracts; Core owned inference/tool loop/approval/summary/SQLite/rules/skills/memory; Tools owned workspace operations; Auth supplied only authorization; Host owned stdio/locks/account/run/workspace assembly; Avalonia Desktop rendered real Host events. One active writer per workspace and durable events before UI delivery prevented disconnect driven replay. Independent changes required worktrees.

SDK `11.0.100-rc.3.26522.148`, net11.0 and C# 15 were pinned. Closed hierarchies, async streams/cancellation, source generated System.Text.Json metadata, FrozenDictionary and SQLite were used, without claiming Native AOT or measured runtime gains. Avalonia/Markdig/code preview/PTY components supplied UI; bounded Markdown fallback, persistent language/reduced motion and draft preservation were product additions. ResourceManager/.resx supported English/Simplified Chinese/system without translating raw content. System reduced motion integration remained absent.

Official OpenAI/Anthropic SDKs owned serialization/SSE; shared SocketsHttpHandler managed connection lifetime/timeouts without cookies/redirects or implicit request replay. Auth used documented dynamic OAuth, state/nonce/PKCE, Microsoft JWT and MSAL native protection, never borrowed login files or external Codex execution. Account/model/endpoint state stayed bound; changing accounts/protocol created a new chat. Existing Codex history stayed read only. Model directory membership did not prove real access. Live ChatGPT/Claude and Linux/macOS vault/device acceptance were still incomplete; real DeepSeek had controlled repair/continuation/vision successes and a retained mixed vision failure.

The historical complete tool set had **seventeen tools/twelve read only**. Files enforced workspace/link/Git boundaries, UTF-8 and SHA-256 conflicts; reads were sixteen MiB, edit/syntax/GUI whole file previews one MiB. Search/glob/read pages had explicit cursors/snapshots/truncation; scans were at most 2,000 files/32 MiB and regex timeout 100 ms/line. Roslyn symbols were syntax only. Patches supported exact unique multi file Add/Update/Delete/Move, all path rules, one MiB/file/eight MiB transaction and controlled rollback, without crash atomicity.

Commands ran with user privileges, not an OS sandbox. Noninteractive runs and background IDs/logs were owned per run/workspace, max four active commands and four MiB retained logs. Cancellation/run end/Host EOF stopped owned processes; detached descendants were outside guarantees. Restart read logs without PID reattachment or tool replay. Evidence tracked all moved/deleted targets and invalidated changed observations; zero exit never proved task success. Candidate procedures rechecked source events/file versions. Event I/O was not yet paginated. Steering at model/tool boundaries skipped unexecuted old actions; stop did not undo completed changes, and unconsumed queued input/unknown crash actions stayed explicit.

State lived under LocalApplicationData/Workbench, containing private sessions/code/tool output/attachments/native continuation, nonsecret settings/locks and protected OAuth data. API secrets/tokens were not saved as GUI preferences or continuation. `--data-dir` / `--workspace` supplied isolation. Builds/packages used exact locks, self contained Host+Desktop, RID metadata and SHA-256. Signed installers, updates/macOS notarization were absent. SDKs/raw runtime artifacts were ignored by Git.

Evidence progression was explicitly historical: model layer **190 passed/one skipped**, continuity **239/one skipped**, unified kernel **272 passed**, then tools **366/366**, with corresponding package counts **310/306/306**, **318/314/314**, **331/327/327** and **341/337/337**. Earlier product packages **303/299/299** had Windows Host/GUI smoke checks; Linux/macOS were not run. Local SSE Host/lifecycle/continuity/Responses/tool fixtures used no cloud calls. Earlier authorized DeepSeek runs and real Windows UI/localization/terminal interactions are separately recorded, including incomplete manually stopped tasks and remaining stress/IME/scale tests; no fixture or stopped task was promoted to full autonomous success.

The roadmap prioritized reliable coding, fixed repository/fault baselines and recoverable context; then controlled web/MCP/browser/data artifacts; then independent history/memory/offline optimization/prefetch experiments. Planned dataset sizes and competitor comparisons were not completed outcomes or paid budget authorization. Three platform device testing was deliberately deferred, while cross platform packaging continued. Full self learning, remote skills and claimed research/performance superiority were outside the snapshot.

This English page summarizes the preserved source. [The full Chinese record](implementation-status.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](../research/naming.md)
- [Original reference 2](tools-upgrade-validation.md)
- [Original reference 3](unified-kernel-validation.md)
- [Original reference 4](../research/dotnet-reuse-audit.md)
- [Original reference 5](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions)
- [Original reference 6](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
- [Original reference 7](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations)
- [Original reference 8](../src/Workbench.Core/DEPENDENCIES.md)
- [Original reference 9](../src/Workbench.Core/Licenses/Responses/PROVENANCE.md)
- [Original reference 10](../src/Workbench.Core/ANTHROPIC.md)
- [Original reference 11](../src/Workbench.Desktop/DEPENDENCIES.md)
- [Original reference 12](model-core-validation.md)
- [Original reference 13](continuity-validation.md)
- [Original reference 14](../research/opensource.md)
- [Original reference 15](../research/platform.md)
- [Original reference 16](../research/papers.md)
- [Original reference 17](../research/architecture-roadmap.md)
