**English** | [简体中文](opensource.zh-CN.md)

# Open source agent mechanisms for a coding first C# desktop

Research snapshot **2026-10-30 (Asia/Shanghai)**. This is a source/architecture survey, without implementation or performance measurements in that research round. Product scope was coding correctness first, with bounded document/data/browser work later. C# module/type names in the original are design suggestions rather than existing upstream APIs.

The matrix distinguishes source inspection from documentation review. Pi (MIT) contributed a small composable loop, tool events, steering/followup, session trees and compaction; Codex (Apache 2.0) contributed Host/client thread/turn/item, bidirectional approval/cancel protocols; OpenCode (MIT) contributed queues, execution ownership and restart reconciliation. Aider (Apache 2.0) offered budgeted symbol/reference PageRank repo maps; mini-swe-agent (MIT) offered a minimal model/environment/loop and frozen evaluation baseline. OpenHands (MIT) separated Canvas/Agent Server/SDK/schedulers/backends; Gemini CLI/Cline (Apache 2.0) informed checkpoints, diff review and plan/act permissions. Open Interpreter (Apache 2.0) informed model specific/pluggable harnesses, not a mandatory second execution engine.

Goose (Apache 2.0) provided parameterized recipes and MCP extension lifecycle for a few adjacent work templates. browser-use (MIT) provided observe/action/result, repeated action detection and cancellation; UI-TARS/Agent TARS (Apache 2.0) provided operators and DOM/visual channels for later computer work. OpenClaw (MIT) informed deferred gateway/device identity/idempotence; Agent S (Apache 2.0) separated planning/grounding/offline evolution; OpenCUA (MIT root) informed synchronized demonstrations/evaluation with separate data/model licenses. Main repository licenses do not cover all submodules/assets/services/weights.

Specific source samples pinned Pi, Codex and OpenCode revisions in the original links, including Codex protocol `02ca1e0d990e496366bda7c30e2981cf296783ce` and OpenCode `60072747957926e7aeec7e911fe6e23abe9bf830`. Source porting must record the actually used commit/file/function/differences, retain notices and separate copied code from behavioral inspiration. Broad source inspection is not a full security/implementation audit.

Key proposed .NET mappings were immutable/versioned contracts, Task/CancellationToken/Channels/async streams, scoped serialized runs, SQLite durable state, BCL Process/file/hash/HTTP/JSON, official provider/MCP SDKs, mature Roslyn/editor/PTY/browser components. Avoid copying TS/Python event emitters, stores, transport stacks, parsers, runtimes or whole package trees when .NET already meets the contract. Custom code requires an explicit missing capability, alternatives and validation.

The loop must process steering at known boundaries, persist before emitting events, preserve caller ownership and reconcile unknown side effects before any retry. Repo maps are contextual views rather than truth, with freshness/budget constraints. Checkpoints are joint file/session/tool state, not permission to rerun old commands. Browser action completion must be checked against current state; repeated actions trigger a bounded change in strategy rather than infinite retries. Receipts, revisions, logs and independent checks decide completion rather than one model answer.

Stale impressions were corrected: current OpenHands/OpenInterpreter architectures differ from older descriptions; UI-TARS local platform claims do not prove Linux native control. OpenCUA was NeurIPS 2025 Spotlight, Agent S3 TMLR 2026, S4 a 2026 arXiv work; these were not interchangeable 2026 top conference Oral claims. Domain/platform/model licenses and privileges need separate verification.

Implementation phases were shared model/tool/session contracts and a minimal coding/fault baseline first, context/rules/skills/diff/checkpoints next, bounded MCP/recipes/browser afterward, and full desktop/gateway/multiagent only with independent need and evidence. Acceptance must preserve user edits, explicit approvals, cancellation/descendant cleanup, stream failure/native state and cold recovery. The survey did not conclude that any competitor's success rate, paper effect or shorter source code automatically transferred to this product.

This English page summarizes the preserved source. [The full Chinese record](opensource.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](https://github.com/earendil-works/pi)
- [Original reference 2](https://github.com/earendil-works/pi/blob/main/LICENSE)
- [Original reference 3](https://github.com/openai/codex)
- [Original reference 4](https://github.com/openai/codex/blob/main/LICENSE)
- [Original reference 5](https://github.com/anomalyco/opencode)
- [Original reference 6](https://github.com/anomalyco/opencode/blob/dev/LICENSE)
- [Original reference 7](https://github.com/Aider-AI/aider)
- [Original reference 8](https://github.com/Aider-AI/aider/blob/main/LICENSE.txt)
- [Original reference 9](https://github.com/SWE-agent/mini-swe-agent)
- [Original reference 10](https://github.com/SWE-agent/mini-swe-agent/blob/main/LICENSE.md)
- [Original reference 11](https://github.com/OpenHands/OpenHands)
- [Original reference 12](https://github.com/OpenHands/OpenHands/blob/main/LICENSE)
- [Original reference 13](https://github.com/google-gemini/gemini-cli)
- [Original reference 14](https://github.com/google-gemini/gemini-cli/blob/main/LICENSE)
- [Original reference 15](https://github.com/cline/cline)
- [Original reference 16](https://github.com/cline/cline/blob/main/LICENSE)
- [Original reference 17](https://github.com/openinterpreter/openinterpreter)
- [Original reference 18](https://github.com/openinterpreter/openinterpreter/blob/main/LICENSE)
- [Original reference 19](https://github.com/aaif-goose/goose)
- [Original reference 20](https://github.com/aaif-goose/goose/blob/main/LICENSE)
- [Original reference 21](https://github.com/browser-use/browser-use)
- [Original reference 22](https://github.com/browser-use/browser-use/blob/main/LICENSE)
- [Original reference 23](https://github.com/bytedance/UI-TARS-desktop)
- [Original reference 24](https://github.com/bytedance/UI-TARS-desktop/blob/main/LICENSE)
- [Original reference 25](https://github.com/openclaw/openclaw)
- [Original reference 26](https://github.com/openclaw/openclaw/blob/main/LICENSE)
- [Original reference 27](https://github.com/simular-ai/Agent-S)
- [Original reference 28](https://github.com/simular-ai/Agent-S/blob/main/LICENSE)
- [Original reference 29](https://github.com/xlang-ai/OpenCUA)
- [Original reference 30](https://github.com/xlang-ai/OpenCUA/blob/main/LICENSE)
- [Original reference 31](https://github.com/OpenHands/software-agent-sdk)
- [Original reference 32](https://github.com/endolith/open-interpreter)
- [Original reference 33](https://github.com/OpenHands/OpenHands/blob/main/README.md)
- [Original reference 34](https://github.com/openinterpreter/openinterpreter/blob/main/README.md)
- [Original reference 35](https://github.com/simular-ai/Agent-S/blob/main/README.md)
- [Original reference 36](https://github.com/xlang-ai/OpenCUA/blob/main/README.md)
- [Original reference 37](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/agent/src/agent-loop.ts)
- [Original reference 38](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/coding-agent/docs/session-format.md)
- [Original reference 39](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/coding-agent/docs/compaction.md)
- [Original reference 40](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/coding-agent/docs/rpc.md)
- [Original reference 41](https://learn.chatgpt.com/docs/app-server)
- [Original reference 42](https://github.com/openai/codex/tree/02ca1e0d990e496366bda7c30e2981cf296783ce/codex-rs/app-server-protocol/src/protocol/v2)
- [Original reference 43](https://github.com/openai/codex/tree/02ca1e0d990e496366bda7c30e2981cf296783ce/codex-rs/app-server)
- [Original reference 44](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/session/inbox.ts)
- [Original reference 45](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/session/execution.ts)
- [Original reference 46](https://github.com/OpenHands/software-agent-sdk/blob/main/README.md)
- [Original reference 47](https://github.com/Aider-AI/aider/blob/main/aider/repomap.py)
- [Original reference 48](https://github.com/SWE-agent/mini-swe-agent/blob/main/src/minisweagent/agents/default.py)
- [Original reference 49](https://github.com/google-gemini/gemini-cli/blob/main/docs/cli/checkpointing.md)
- [Original reference 50](https://github.com/cline/cline/blob/main/README.md)
- [Original reference 51](https://github.com/aaif-goose/goose/blob/main/README.md)
- [Original reference 52](https://github.com/aaif-goose/goose/blob/main/documentation/docs/guides/recipes/recipe-reference.md)
- [Original reference 53](https://github.com/aaif-goose/goose/blob/main/crates/goose/src/recipe/manifest.rs)
- [Original reference 54](https://github.com/aaif-goose/goose/blob/main/crates/goose/src/recipe/validate_recipe.rs)
- [Original reference 55](https://github.com/aaif-goose/goose/tree/main/crates/goose/src/agents/extension_manager)
- [Original reference 56](https://github.com/bytedance/UI-TARS-desktop/blob/main/README.md)
- [Original reference 57](https://github.com/bytedance/UI-TARS-desktop/blob/3ac2cb8d946d78d5ff92ad17b318eaafa18c178c/multimodal/gui-agent/operator-browser/src/browser-operator.ts)
- [Original reference 58](https://github.com/browser-use/browser-use/blob/2cc71890e65056b67fdb29d98bb407749c15bb08/browser_use/agent/service.py)
- [Original reference 59](https://github.com/openclaw/openclaw/blob/76985e4c31c4c94c91e7ed79411c8189180b7ea8/docs/concepts/architecture.md)
- [Original reference 60](https://github.com/simular-ai/Agent-S/blob/main/gui_agents/s3/README.md)
- [Original reference 61](https://github.com/simular-ai/Agent-S/blob/main/agent_s4/README.md)
- [Original reference 62](https://github.com/xlang-ai/OpenCUA/blob/main/data/README.md)
- [Original reference 63](architecture-roadmap.md)
