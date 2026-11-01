# Provider setup and protected API credentials

New Desktop settings start with no authentication mode, endpoint or model selected. The first connection dialog offers a ChatGPT account or API key. Account authentication continues to supply credentials to the same Host/Core kernel. Existing settings without the additive `onboardingCompleted` marker migrate without another onboarding requirement; their backend, model and environment-variable settings remain. Older Auto OpenAI settings explicitly retain Chat Completions. Fresh address detection selects native Responses for the official OpenAI endpoint.

The API tab offers named presets for OpenAI, Anthropic, DeepSeek, OpenRouter and Mistral, or an explicitly entered custom endpoint/protocol. Identification is an **address-based hint**, never a claim to identify an arbitrary service or model from its API key. Model IDs can be entered manually. Only the explicit **Fetch models** action calls the selected endpoint; no inference, model benchmark or paid acceptance call is made by discovery.

## Existing components reused

`ProviderCatalog` makes metadata-only GET requests using the existing `ModelHttpTransport.SharedClient`, its `SocketsHttpHandler` connection pool, disabled redirects/cookies and System.Text.Json. It adds no HTTP/SSE stack, SDK replacement, model registry service or capability benchmark. Inference still uses the existing official OpenAI/Anthropic SDK adapters through `AgentModelFactory.Create(provider, resolvedKey, httpClient)`.

The direct bounded metadata projection preserves useful third-party fields that a single vendor SDK's model-list DTO does not universally represent. Unknown image capabilities remain null; only explicit `input_modalities`, OpenRouter `architecture.input_modalities`, Mistral `capabilities.vision`, or Anthropic `capabilities.image_input.supported` set the catalog image flag. DeepSeek effort metadata is retained when explicitly present. A catalog listing is not proof of inference entitlement, coding-tool support, multimodal behavior or compatibility of every listed model with the selected protocol.

The Host resolves a saved credential only for its bound normalized endpoint and protocol. `ProviderSettings.CredentialProfileId` is a nonsecret reference; the settings schema has no raw key field. Existing `ApiKeyEnvironmentVariable` usage remains supported when no saved profile is selected. Key material is never assigned to process-global environment variables, worker arguments, run options, conversation events or desktop JSON settings. Remote Hosts own separate credentials; changing the connected Host does not forward a local saved key.

Replacing an existing bound credential retains its profile ID, including at capacity. A replacement naming another endpoint/protocol is refused; an explicitly saved stale/deleted reference receives a fresh ID. Repeated key rotation does not leave unreachable credential entries.

`ApiCredentialStore` uses the same mature native **MSAL Storage 4.92.0** facility as [ProtectedAuthStore](../../Workbench.Auth/Storage/README.md): Windows DPAPI, macOS Keychain and Linux Secret Service/libsecret. It uses a separate `api-credentials` namespace, storage record and exclusive BCL lock, so it can coexist with the OAuth store's lifetime lock. Windows commits a fully DPAPI-protected temporary file by rename; other systems update a native vault record. An unavailable vault fails explicitly without plaintext fallback. Serialization byte buffers are zeroed after use. Immutable .NET strings are transient managed objects; the implementation does not claim guaranteed erasure of every managed copy.

The GUI masks key entry, never loads the saved secret into a text box, clears it after protected save and on close, and clears pending key text when the service address/protocol changes. Explicit Fetch models may use unsaved key text for that one metadata request. RPC credential payload classes redact `ToString`; errors never retain server bodies, credential-bearing headers, inner network exceptions or redirected target URLs. StdIO runs through the owned local Host; remote setup uses its authenticated SSH/service transport.

## Bounds and primary contracts

Catalog requests have a 20-second cancellation budget, at most four pages, 2 MiB per response page and 1,000 returned model IDs. IDs and display names are bounded. Anthropic's `has_more`/`last_id` pagination stays on the same configured endpoint; redirects are errors, and requests are never automatically retried. Unsupported catalogs leave manual model entry available. The protected store permits at most 64 credential profiles; API key text is limited to 8,192 characters without control characters.

Primary service documentation inspected for this implementation:

- [OpenAI list models](https://developers.openai.com/api/reference/resources/models/methods/list): authenticated `GET /models`, basic model identity metadata.
- [Anthropic list models](https://platform.claude.com/docs/en/api/models/list): native model catalog, capability metadata and bounded cursor pagination. Discovery uses the API version already used by the pinned native adapter.
- [DeepSeek list models](https://api-docs.deepseek.com/api/list-models/): explicit input modalities and effort metadata when supplied.
- [OpenRouter model catalog](https://openrouter.ai/docs/api/api-reference/models/list-all-models-and-their-properties): OpenAI-compatible catalog and explicit architecture metadata.
- [Mistral model API](https://docs.mistral.ai/api/endpoint/models): model-list and capability metadata.

These are API behavior references, not source-code ports; no upstream source code was copied. Native storage licensing/provenance remains in [Auth provenance](../../Workbench.Auth/Licenses/PROVENANCE.md).

## Verification boundary

`ProviderSetupTests` uses actual loopback HTTP plus deterministic HTTP handlers to verify selected-endpoint authentication, metadata-only GETs, pagination, missing capabilities, redirect/error redaction, fresh settings and existing-settings migration. Windows native-vault fixtures verify encrypted persistence, endpoint/protocol rejection, independent OAuth/API locks, deletion and reopening. Other OS vaults require separate live platform acceptance. Headless UI fixtures check the actual compiled connection dialog; no desktop is opened for verification. No real provider key, inference request or paid model call is required by these fixtures.
