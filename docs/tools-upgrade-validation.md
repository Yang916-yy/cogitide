**English** | [简体中文](tools-upgrade-validation.zh-CN.md)

# Tool upgrades and background acceptance record

Historical round dated **2026-10-30**, internal name Cogito. The final strict build had zero warnings/errors and **366/366 tests passed**. An initial worker unexpectedly exited; later full runs and twelve targeted repetitions passed, without proving the original intermittent cause had been identified or fixed. Raw failures remain part of the record.

The then complete Host exposed **seventeen tools/twelve in read only mode**: five navigation, three editing, four command, one Git and four kernel knowledge/continuity tools. Read only allowed stopping/reading owned commands and internal candidate experience; it was application policy, not an OS sandbox. API and account authentication used the same AgentEngine.

Patches added Add/Update/Delete/Move with exact unique hunks, current SHA-256, all path/rule preflight, recheck and controlled rollback/recovery files. Bounds were 64 source operations/512 hunks/one MiB per file/eight MiB total; power loss atomicity was not promised. Navigation added sixteen MiB UTF-8 reads, 12,000 character pages, line/character cursors, whole file hashes, content/inventory snapshots, regex/glob/context and explicit truncation. Search scanned at most 2,000 files/32 MiB with 100 ms per line regex timeout; Roslyn symbols were a syntax outline, not semantic LSP.

Owned background commands distinguished running/terminal/interrupted states, retained at most four MiB UTF-8 cursor logs, stopped on cancellation/run end/parent pipe loss, and never attached/replayed old PIDs. No model PTY, cross Host running process recovery or guarantee for deliberately detached descendants existed. Multi file/move/delete evidence was persisted and revalidated; zero exit was only an observation. Experience saving checked the same 16,000 character single recall budget. Cleanup always attempted resource stop/ownership release; invalid terminal state and surrogate splitting were rejected.

Generated local HTTP/SSE repositories and real Host stdio supplied verification, with no paid model, real OAuth or visible desktop. Earlier full runs were **364 pass/one failure**, **365/365**, then **366/366**. A separate initially empty large output observation was retained; later cursor/terminal race changes and passing reruns did not establish both original causes. The final packaged Windows tool chain passed **nine checks, 26 local requests, three file pages, twelve Unicode log pages and three approvals**, including two descendant levels exiting after Host kill and cold retained logs without replay. Continuity, Responses, Host and seven lifecycle categories also passed.

Historical packages contained **341/337/337 files**, matched six source locks each, native architecture, runtime and notices. Windows Host used its bundled runtime; Linux/macOS remained structure/hash only. Fixed sources: OpenCode patch `810c0eba1a7348036f8bdf61e6632d0c36c6876c`, OpenCode navigation `60072747957926e7aeec7e911fe6e23abe9bf830` and Codex behavior `788b9d7bf6e37f735647b7f84b556c2a807df4d8`. Reuse included BCL I/O/Regex/Process, Git and Microsoft.Extensions.FileSystemGlobbing 10.0.14 rather than full upstream runtimes.

The report did not establish Codex/OpenCode success parity, .NET speed/memory benefits or paper gains. Full LSP, subagents, search/MCP and browser extensions were outside this historical seventeen tool set. CI steps were configured but remote CI was not triggered; configuration was not three platform execution evidence. Follow later integration documentation for subsequent capabilities.

This English page summarizes the preserved source. [The full Chinese record](tools-upgrade-validation.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](../artifacts/build-tools-upgrade.log)
- [Original reference 2](../artifacts/test-results/tools-upgrade-final.trx)
- [Original reference 3](../artifacts/test-results/tools-upgrade-final-rerun.trx)
- [Original reference 4](../artifacts/test-results/tools-upgrade-complete.trx)
- [Original reference 5](../artifacts/test-results/tools-upgrade-release.trx)
- [Original reference 6](../artifacts/test-results/tools-upgrade-verified.trx)
- [Original reference 7](../artifacts/verification/tools-upgrade/d8345a2b1d534d6eaf252e4d12b6769b/result.json)
- [Original reference 8](../artifacts/verification/tools-upgrade/41e34c77e206412c86defe4a4baeb354/result.json)
- [Original reference 9](../artifacts/continuity/14c9ab7a7f7c415ab065476f6675a5a8/result.json)
- [Original reference 10](../artifacts/protocol-/d311f8026fae42a7b6479bd8a43445ee/result.json)
- [Original reference 11](../artifacts/verification/unified-responses/fce6e67dc88440bc97ca5c89f0b57492/result.json)
- [Original reference 12](../artifacts/lifecycle/05ef598c356d41c1965761de601117fd/result.json)
- [Original reference 13](../artifacts/release-checks/tools-upgrade-preview-final.json)
- [Original reference 14](../src/Workbench.Tools/Licenses/Patch/PROVENANCE.md)
- [Original reference 15](../src/Workbench.Tools/Licenses/Navigation/PROVENANCE.md)
- [Original reference 16](../src/Workbench.Tools/Licenses/Commands/PROVENANCE.md)
- [Original reference 17](../research/opensource.md)
- [Original reference 18](../research/dotnet-reuse-audit.md)
- [Original reference 19](../research/papers-recheck-2026.md)
