**English** | [简体中文](README.zh-CN.md)

# Model provider adapters

This module maps provider APIs to `IAgentModel`: requests, streaming responses and native continuation. It does not execute agent tools, own project memory or authorize tasks. `AgentModelFactory` is shared by Host and offline evaluation; `ModelCapabilities` validates provider/endpoint/image/reasoning/state compatibility; `ModelHttpTransport` supplies the shared BCL connection pool and connection/redirect/retry policy.

`OpenAiCompatibleModel` uses official **OpenAI .NET 2.14.0** Chat Completions plus DeepSeek reasoning compatibility. `OpenAiResponsesModel` uses the same official SDK for complete output items, encrypted reasoning, namespaces and final state validation. `AnthropicModel` uses official **Anthropic 13.3.0** Messages, thinking, signed blocks, images and tools. SDK pipelines own HTTP/SSE; `SocketsHttpHandler` owns transport. Exact versions, protocol gaps and locks are in [DEPENDENCIES](../DEPENDENCIES.md), [ANTHROPIC](../ANTHROPIC.md) and `packages.lock.json`.

DeepSeek missing reasoning backfill adapts Pi commit `f09d01b85e0384647fcab6779d35ab9dabe04be3`; Responses replay references Pi `e7a0df27c09d11bf4161d3605246cbf57a129cb2`. [Responses provenance](../Licenses/Responses/PROVENANCE.md) records exact files and retained MIT licenses. Only these behaviors were adapted; Pi's full transport, model catalog, pricing and provider package were not copied.

Private reasoning is never rendered as answer text or rewritten; signed/opaque state stays bound to its endpoint/model. Incomplete, truncated or failed responses cannot commit calls or continuation. Requests are not silently replayed. Account authentication is separate [Auth](../../Workbench.Auth/Accounts/README.md), supplying credentials to this same kernel.

**Do not reinvent infrastructure.** Use an official/mature C# SDK first, shared transport, existing image checks and event contracts. New adapters must identify SDK gaps, protocol sources and serialization/final state tests. Local SDK fixtures and real account entitlement are distinct acceptance evidence.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](AgentModelFactory.cs)
- [Original reference 2](OpenAiCompatibleModel.cs)
- [Original reference 3](OpenAiResponsesModel.cs)
- [Original reference 4](AnthropicModel.cs)
- [Original reference 5](ModelCapabilities.cs)
- [Original reference 6](ModelHttpTransport.cs)

`ProviderCatalog` adds explicit metadata discovery through the existing BCL transport. [Provider configuration](PROVIDER-CONFIGURATION.md) documents fresh-install onboarding, endpoint-based presets, protected credential references, catalog bounds, primary API sources and deterministic/native-vault verification boundaries.
