# Model adapter provenance

Verified on 2026-10-30. This file distinguishes shipped dependencies, small behavior ports, and research references.

## Official transport and BCL

`OpenAiCompatibleModel` uses OpenAI .NET **2.14.0**, MIT, repository commit
[`2e77b08828145f658ec04e49aec87abb1543c553`](https://github.com/openai/openai-dotnet/tree/2e77b08828145f658ec04e49aec87abb1543c553).
`System.ClientModel` **1.15.0** supplies SDK `JsonPatch` extensions for provider-specific fields and the HTTP pipeline.
SDK code owns request serialization, streaming framing and parsing; Cogito does not implement an SSE parser.
`StreamingChatCompletionUpdate.Patch` propagates nested paths to choice/delta objects in this pinned SDK version,
so DeepSeek `reasoning_content` does not require reserializing every chunk or reflection over SDK internals.
Use `TryGetJson` for these nested reads: `Contains` alone does not follow the child-model read propagators.
Replay values use explicit JSON `BinaryData` because SCM 1.15's string patch writer rejects empty encoded values.
Both SDK behaviors were reproduced and verified with actual `ChatClient` streaming/request fixtures.
`ChatMessageContentPart` serializes image inputs. `ChatReasoningEffortLevel` configures generic compatible APIs.
The existing BCL `SocketsHttpHandler` owns pooling, decompression and connection timeouts; automatic redirects and model retries stay disabled.

`ModelCapabilities` performs endpoint/provider/image/continuation preflight checks. Shared `ImageInputs` validates
base64 and image signatures; BCL `System.Buffers.Text.Base64.IsValid(ReadOnlySpan<char>, out int)` sizes aggregate image
data without allocating another decoded array. This is use of available .NET APIs, not a claim that the API originated in .NET 11.

## Small Pi behavior port

The missing `reasoning_content` → empty string compatibility rule in `ToSdkMessage` is adapted from
Pi's `convertMessages` behavior, at fixed commit
[`f09d01b85e0384647fcab6779d35ab9dabe04be3`, `packages/ai/src/api/openai-completions.ts`, lines 1375–1381](https://github.com/badlogic/pi-mono/blob/f09d01b85e0384647fcab6779d35ab9dabe04be3/packages/ai/src/api/openai-completions.ts#L1375-L1381).
The adaptation applies to DeepSeek assistant turns, including turns without tools. Cogito retains actual reasoning
in endpoint/model-scoped opaque continuation state and commits it only after a complete valid response. It is not rendered as answer text.
`PiCompatibilityBackfillsEmptyReasoningOnLegacyAssistantMessages` tests the ported behavior; the multi-turn test also
checks preservation of reasoning across tools and ordinary assistant replies. We did not port Pi's transport or full provider module.

Pi license, from that commit:

```text
MIT License

Copyright (c) 2025 Mario Zechner

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Protocol facts and comparison sources

- [DeepSeek thinking](https://api-docs.deepseek.com/guides/thinking_mode/): explicitly send thinking enabled/disabled;
  low/high/max effort; when tools are supplied preserve reasoning on **all** previous assistant turns.
  Cogito keeps `ReasoningEffort="none"` for old configurations and deliberately defaults thinking off.
- [DeepSeek chat API](https://api-docs.deepseek.com/api/create-chat-completion/) and
  [models](https://api-docs.deepseek.com/quick_start/pricing/): `deepseek-flash` supports vision; `deepseek-v4-pro` does not.
  The documented model output ceiling is 384K tokens; app requests retain a much smaller default.
  We currently accept inline JPEG/PNG/WebP/GIF user images within shared application limits.
- [DeepSeek caching](https://api-docs.deepseek.com/guides/kv_cache/): automatic prefix caching and
  `usage.prompt_cache_hit_tokens` are reported as cached input, not extra input. Cache hits are best-effort.
  Completion reasoning tokens are part of completion tokens, not additional charged output in the total.
- OpenCode comparison only, no code copied:
  [`2273b607bc6a6cd9366ad2454df79b72fc73b1c9`, `packages/ai/src/providers/deepseek.ts`](https://github.com/anomalyco/opencode/blob/2273b607bc6a6cd9366ad2454df79b72fc73b1c9/packages/ai/src/providers/deepseek.ts)
  delegates transport to its common OpenAI-chat protocol and overrides `max_tokens` compatibility.
  Cogito takes the same architectural separation using the official C# SDK.

Validation is recorded by serialized-request and synthetic stream fixtures in `ModelCapabilityTests`,
plus the existing `ProviderStreamingTests` and `ProviderTransportTests`. These fixtures establish local adapter behavior;
they do not constitute a live vendor compatibility result or a quality benchmark.
