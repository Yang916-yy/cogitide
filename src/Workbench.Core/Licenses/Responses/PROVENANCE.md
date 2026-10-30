# Responses adapter provenance

Implemented 2026-10-30. `OpenAiResponsesModel` is a model inference adapter for Cogito's own `AgentEngine`. It does not delegate tools, compaction, skills, experience or execution policy to Codex app-server.

## Mature implementation adapted

Pi, MIT, fixed commit `e7a0df27c09d11bf4161d3605246cbf57a129cb2`:

- [`packages/ai/src/api/openai-responses.ts`](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/ai/src/api/openai-responses.ts): stateless `store:false`, reasoning encrypted-content inclusion and ChatGPT-plan request-field filtering.
- [`packages/ai/src/api/openai-responses-shared.ts`](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/ai/src/api/openai-responses-shared.ts): `convertResponsesMessages` preserves reasoning item identity/signatures and function namespace, `processResponsesStream` checks terminal events and unfinished tool calls, and `backfillReasoningSignatures` obtains ciphertext supplied only in terminal output.
- Upstream regression references: `openai-responses-chatgpt-sign-in.test.ts`, `openai-responses-namespace.test.ts`, `openai-responses-terminal-event.test.ts` at the same revision. Cogito tests independently drive the actual C# SDK and engine/store.

These mechanisms are adapted into C# records, async streams, typed SDK response events and bounded dictionaries. The exact upstream MIT notice is retained as `Pi-LICENSE.txt`. This is a focused behavior/module port; Pi's model catalog, pricing, WebSockets, cache affinity, dynamic tool delta/configuration updates and cross-provider ID rewriting are not copied.

## Native SDK and official contract

OpenAI .NET **2.14.0** supplies `ResponsesClient`, request serialization, HTTP/SSE parsing and native input/output models. The existing .NET `SocketsHttpHandler` transport is reused, with model retries disabled. The pinned SDK marks its Responses surface `OPENAI001`; that diagnostic is suppressed only in this adapter and covered by serialized-request/stream tests. SDK `JsonPatch` supplies documented namespace tools and lossless input-item replay not represented in its public typed properties. There is no additional HTTP or SSE implementation.

Official documents checked on 2026-10-30:

- [Models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference): OAuth bearer calls go to the public `https://api.openai.com/v1/responses`, with `store:false`, `stream:true`, and completion checked through `response.completed`.
- [Preview limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations): request context is explicit, `previous_response_id` and `max_output_tokens` are omitted on the plan route, developer instructions are used, and function tools are grouped in the `cogito` namespace. API-key mode keeps ordinary function tools and the configured output-token limit.

OAuth credentials come from the account service immediately before each inference request. The adapter does not read Codex credentials, refresh tokens itself, log access tokens or retry inference after an authentication/usage failure. OAuth is pinned to the public OpenAI origin. Continuation is bound to endpoint, model and the non-secret account/workspace scope supplied by the caller; that scope is checked before token acquisition or HTTP.

## Integration safeguards and scope

Only completed response output items are persisted. Reasoning ciphertext, message phases, item IDs and function namespaces remain opaque native state and are replayed in order. Completion must agree with streamed identity/arguments and every function call must have an `output_item.done` event. Failed/incomplete streams, cancellation, wrong namespaces and late plan-limit errors cannot release tools or partial continuation. Hidden reasoning is not displayed or sent to the summary model.

This adapter supports client-executed JSON function tools, text and image inputs. Server-side web search, hosted MCP, computer use, audio, files upload, native `tool_search`, custom grammar tools and provider conversation storage are not enabled. Current account/model entitlement still requires a successful inference; a model listing is not proof of access. Cross-model/account continuation is rejected rather than silently stripped or rewritten.

`OpenAiResponsesTests` verifies the actual SDK wire contract, OAuth field filtering, original item and namespace replay, credential refresh callback use, safe errors, incomplete stream handling, and own-engine summary/tool execution with SQLite cold restart. These are deterministic protocol/integration checks, not a real ChatGPT entitlement or paid-account test.
