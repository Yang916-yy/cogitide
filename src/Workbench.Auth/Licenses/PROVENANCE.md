# Official ChatGPT authentication and mature .NET components

Implemented against official documentation read on 2026-10-30. This library provides account authorization, protected credential storage and model discovery only. It contains no agent loop, tools, Codex app-server, borrowed Codex client ID, private backend endpoint or extraction of another application's credentials.

## Official contract

- [Registration and sign-in](https://developers.openai.com/siwc/token-sharing-open-source/sign-in): first-time `dynamic_agent_client`, stable `ext_agent_host_id`, actual application name, issued client ID, exact 127.0.0.1 callback URI, state/nonce/PKCE, validated identity and granted scopes.
- [Accounts and sessions](https://developers.openai.com/siwc/token-sharing-open-source/profiles-and-sessions): separate subject/client registrations, account selection, signed-out metadata retention, serialized refresh-token rotation, remote revocation and explicit unconfirmed-revocation feedback.
- [Token reference](https://developers.openai.com/siwc/token-sharing-open-source/token-reference): access/refresh token sets and expiry fields. OpenAI's authentication metadata remains opaque.
- [Models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference): a live account-specific catalog from `https://api.openai.com/v1/models`, displayable entries in server order, no bundled catalog treated as entitlement.
- [Preview limitations](https://developers.openai.com/siwc/token-sharing-open-source/preview-limitations): inference uses the public Responses endpoint through the separate model adapter; this auth library never sends a task to an external agent engine.

These documents describe open-source/local applications; paid or remotely hosted distribution requires the applicable official onboarding. No real account authorization is claimed from offline protocol fixtures.

After a callback passes state validation, an issued client ID is retained separately as unvalidated pending-registration metadata before code exchange. If the code is rejected or the network result is unknown, retry authorization uses that ID with fresh state/nonce/PKCE. It never becomes an authenticated account or inference credential until signed identity validation succeeds. Explicitly adding a new account starts a new dynamic registration; selecting a saved account clears the pending selection.

## Reused implementation

JWT signature, issuer, audience and lifetime validation uses **Microsoft.IdentityModel.JsonWebTokens 8.23.0**, MIT. Package repository commit: [`8b16f418b3d587416d293e9ba34c5d310781cdb3`](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/tree/8b16f418b3d587416d293e9ba34c5d310781cdb3). Nonce and selected-account/client checks are application protocol checks on the already validated identity. JWT parsing and cryptographic verification are not custom implementations. Its MIT notice is retained verbatim as `IdentityModel-MIT.txt`.

Protected storage uses **Microsoft.Identity.Client.Extensions.Msal 4.92.0**, MIT, repository commit [`2465fc31fa2b481b68660670e2becfd3fba7c631`](https://github.com/AzureAD/microsoft-authentication-library-for-dotnet/tree/2465fc31fa2b481b68660670e2becfd3fba7c631). Its public `Storage` API supplies DPAPI on Windows, Keychain on macOS and libsecret on Linux. No plaintext fallback flag is enabled. MSAL is used for its native storage implementation, not Microsoft account authentication. The original MIT notice is retained as `MSAL-MIT.txt`.

The storage source explicitly does not provide cross-process locking, so `ProtectedAuthStore` owns an exclusive BCL file handle for its lifetime. The Host also owns its data directory. Native vault keys include a hash of the application data directory to isolate installations and generated fixtures. On Windows, the MSAL helper encrypts a temporary file and a BCL atomic rename commits the complete token set; on macOS/Linux, the native vault receives one complete record. The app overrides the helper's older transitive ProtectedData dependency with **System.Security.Cryptography.ProtectedData 10.0.14**. Storage errors fail explicitly without plaintext fallback.

The .NET runtime's [v10.0.14 MIT notice](https://github.com/dotnet/runtime/blob/v10.0.14/LICENSE.TXT) is retained as `DotNet-MIT.txt`. Remote session revocation uses at most two attempts with a short cancellable backoff inside a ten-second total deadline; it always reports unconfirmed revocation if local logout completes without server confirmation.

HTTP, TLS, decompression, loopback serving, random bytes, SHA-256, fixed-time comparison, cancellation, file locks and JSON metadata generation use .NET BCL. There is no socket/HTTP implementation, credential daemon or new database. Redirects, automatic model retries, cookies and secret logging are disabled. Login URLs are returned only to the GUI that explicitly requested login; this service never opens a browser itself.

This is a small integration of documented OAuth behavior with mature C# components, not a wholesale TypeScript port. The old private-token or delegated-Codex flows are deliberately not copied. OAuth protocol fixtures exercise signed identities, state/nonce/PKCE, invalid identity/client/scope handling, rotation, revocation, account separation and local callback cancellation. Native Windows storage is checked separately; macOS/Linux vault behavior still requires those platforms.
