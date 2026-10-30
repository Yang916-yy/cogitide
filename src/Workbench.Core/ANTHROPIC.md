# Anthropic native provider

Reviewed against official sources on 2026-10-30. Package `Anthropic` is pinned to
`13.3.0` in `Workbench.Core.csproj` and the NuGet lock files. This is Anthropic's
official C# SDK, not the older community package with the same name.

- Package: https://www.nuget.org/packages/Anthropic/13.3.0
- Fixed upstream revision: https://github.com/anthropics/anthropic-sdk-csharp/tree/a0cdd4c2cd909c5ae29ed939a8ad6e1568c0377a
- Tag: `Anthropic-v13.3.0`; API version selected by that SDK: `2026-10-21`.
- SDK guide: https://platform.claude.com/docs/en/cli-sdks-libraries/sdks/csharp

## Reuse and license

The adapter calls the package directly. No upstream source was copied or
translated into the application. It uses `AnthropicClient`, generated request
and response models, and `MessageContentAggregator`. The SDK uses the BCL
`System.Net.ServerSentEvents` parser. HTTP connections come from the shared
`SocketsHttpHandler`/`HttpClient` transport. The only local stream logic validates
the execution boundary, ordering and size limits before passing events to the
SDK aggregator; it does not parse SSE or reimplement the network stack.

Upstream changed from MIT to Apache-2.0 at version 13.0.0. The historical MIT
notice continues to cover earlier third-party contributions. Preserve both
upstream notices when distributing the SDK:

- https://github.com/anthropics/anthropic-sdk-csharp/blob/a0cdd4c2cd909c5ae29ed939a8ad6e1568c0377a/LICENSE
- https://github.com/anthropics/anthropic-sdk-csharp/blob/a0cdd4c2cd909c5ae29ed939a8ad6e1568c0377a/LICENSE-MIT

Exact copies are in `Licenses/Anthropic/` and are included in build/publish output.

## Protocol decisions

- Native `/v1/messages`, API-key authentication only. No unofficial Claude
  subscription login. Both an origin endpoint and an endpoint ending in `/v1`
  are accepted. Remote endpoints require HTTPS; automatic redirects are disabled
  by the shared transport.
- Streaming text is visible immediately. Tool calls and continuation state are
  released only after the complete stream, all content-block stops, a valid stop
  reason, and validated tool input. EOF, cancellation, timeout, invalid JSON,
  duplicate call IDs, unregistered tools, and trailing events do not release tools.
- Retries are disabled (`MaxRetries = 0`). The five-minute linked cancellation
  deadline covers reading the streaming body as well as connection setup. The
  SDK's own timeout is also set. Provider response bodies and inner exceptions
  are not included in application errors. SDK content telemetry is disabled.
- The full ordered assistant content array survives JSON checkpoint round-trips.
  Thinking signatures, redacted thinking, citations, and additional block fields
  remain opaque; thinking is not emitted as display text. State is bound to the
  format, endpoint and requested model and checked against visible text/tool calls.
- `ReasoningEffort = none` explicitly disables thinking. The other accepted
  levels (`low`, `medium`, `high`, `xhigh`, `max`) request adaptive thinking and
  pass the level to `output_config.effort`. Support for a particular level remains
  model-specific. Explicit token budgets enable the legacy thinking mode only on
  the known Claude 3.7/4.0/4.1/4.5/4.6 families; Claude 4.7+ requires adaptive mode.
- Local image attachments use typed base64 image blocks and shared MIME/size
  validation. No remote image URL fetching is implemented.
- Total input tokens include uncached input, cache reads, and cache writes;
  separate cache and reasoning counters remain available. Usage deltas are
  cumulative, not additive.
- Server tools, automatic fallback, request replay, remote Files API uploads and
  assistant prefilling after an interrupted response are intentionally outside
  this provider's current contract.

Official references:

- https://platform.claude.com/docs/en/build-with-claude/streaming
- https://platform.claude.com/docs/en/build-with-claude/thinking
- https://platform.claude.com/docs/en/build-with-claude/thinking-steering-and-cost
- https://platform.claude.com/docs/en/build-with-claude/extended-thinking
- https://platform.claude.com/docs/en/build-with-claude/vision
- https://platform.claude.com/docs/en/build-with-claude/prompt-caching

`AnthropicModelTests` sends fixture responses through the actual SDK and captures
the actual SDK requests. These cover signed and redacted reasoning replay,
images, usage, tool-result errors, stream corruption, status sanitization, no
retries, and stalled-stream cancellation/deadline. They do not claim live Claude
account validation or model quality measurements.
