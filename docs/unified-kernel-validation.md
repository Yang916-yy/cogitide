**English** | [简体中文](unified-kernel-validation.zh-CN.md)

# Unified kernel, official OAuth and Responses acceptance

Historical **2026-10-30** round, internal name Cogito: strict build zero warnings/errors, **272/272 tests passed, no failures/skips**. Three self contained packages were checked and packaged Windows Host ran in the background. Real ChatGPT authorization/inference and Linux/macOS device acceptance were not performed; no desktop takeover or authorization on the user's behalf occurred.

The production delegated Codex app server project/transport tests were removed. `api` and `chatgpt` became authentication choices into one AgentEngine, sharing tools, approvals, steering/stop, summaries, scoped rules, skills, experience and SQLite recovery. Account login supplies public Responses inference credentials, not a hosted external execution engine or server side tools.

Auth implemented documented dynamic OAuth registration, state/nonce/PKCE, validated IDs/scopes, multiple independent registrations, cancellation, rotation, selection/logout and protected native storage. Rejected exchanges retained only pending unauthenticated registration metadata. Account binding was saved and rechecked before inference; account switches needed a new chat and cancelled work before waiting for revocation/storage. Old Codex chats remained read only, with continuation creating a new chat without external tool replay. API protocol changes likewise required a new chat.

OpenAI .NET 2.14.0 Responses preserved complete output items, encrypted reasoning, phase/namespaces and terminal validation. Partial/cancelled/mismatched state could not commit. Continuation stayed bound to endpoint/model/nonsecret account identity. ChatGPT used `store:false`, streaming, explicit history, developer instructions and namespace functions, omitting unsupported API key path options. Compaction stayed local and excluded private reasoning. Catalog presence did not prove entitlement; unknown reasoning capabilities used provider defaults. Tokens stayed out of sessions/settings/logs; native vault failures had no plaintext fallback. Windows DPAPI was checked; platform code was not proof of every vault.

Fixed Pi Responses reference `e7a0df27c09d11bf4161d3605246cbf57a129cb2` retained MIT notices, without pricing/WebSockets/full catalogs. JWT/system storage reused Microsoft.IdentityModel.JsonWebTokens 8.23.0 and MSAL extensions 4.92.0. Official protocol sources and compaction/rules provenance are retained below.

The **272 tests** included **nineteen OAuth**, **seventeen Responses**, **seven unified Host** and **22 actual Desktop Headless composer/provider/localization** tests. Local Host tools used five calls/two approvals; local Responses used five requests/two approvals/seventeen original events and cold resume without Codex on PATH. Lifecycle had seven categories; continuity had eleven checks/seven summaries/32 original messages/28 requests. All were generated fixtures, not real account inference.

Packages contained **331/327/327 files**, matched six production source locks each and retained Auth/Responses licenses with no old executor/Headless dependencies. Windows Host loaded packaged .NET 11 RC3 with no developer runtime/key override and passed Unicode/command/persistence/exit checks; Linux/macOS had structural checks only. Published locks were isolated, and switching back to source development required locked restore before building. No model charges were incurred in this round. Earlier DeepSeek successes/failures remain separate records.

Open limitations were real account access, three platform GUI/IME/scale/terminal/vault cleanup, signed installers/macOS notarization/updates and controlled competitor/performance experiments. Engineering integration did not reproduce complete ACE/GEPA training or prove benefit.

This English page summarizes the preserved source. [The full Chinese record](unified-kernel-validation.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
- [Original reference 2](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)
- [Original reference 3](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations)
- [Original reference 4](https://chatgpt.com/settings/usage)
- [Original reference 5](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions)
- [Original reference 6](../src/Workbench.Core/Licenses/Responses/PROVENANCE.md)
- [Original reference 7](../src/Workbench.Core/Licenses/Responses/Pi-LICENSE.txt)
- [Original reference 8](../src/Workbench.Auth/Licenses/PROVENANCE.md)
- [Original reference 9](../src/Workbench.Core/Licenses/Compaction/PROVENANCE.md)
- [Original reference 10](../src/Workbench.Core/PROJECT-REUSE.md)
- [Original reference 11](../artifacts/test-results/unified-kernel-final.trx)
- [Original reference 12](../artifacts/ui-composer/)
- [Original reference 13](../artifacts/protocol-/3807ceb8041c462cb9da47f829bb0503/result.json)
- [Original reference 14](../artifacts/verification/unified-responses/09da1295e7a14541b7bea5535e66aedd/result.json)
- [Original reference 15](../artifacts/lifecycle/ccb0a6bc8b894aa3b7900db8a406a9bc/result.json)
- [Original reference 16](../artifacts/continuity/7467a6626e674a5d90e06db073b675be/result.json)
- [Original reference 17](../artifacts/release-checks/unified-kernel-preview.json)
- [Original reference 18](model-core-validation.md)
- [Original reference 19](continuity-validation.md)
