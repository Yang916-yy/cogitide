**English** | [简体中文](README.zh-CN.md)

# Official account authorization boundary

`IChatGptAccountService` exposes status, login, account selection, logout, credentials and model discovery. `ChatGptAccountService` implements documented dynamic registration and callback handling, state/nonce/PKCE, account binding, serialized refresh and the live model catalog. Authentication supplies the shared [Models](../../Workbench.Core/Models/README.md) adapter; it does not delegate tasks to an external agent.

The documented protocol and detailed sources are in [Auth provenance](../Licenses/PROVENANCE.md). No borrowed Codex client ID, private backend or extraction of existing application credentials is used. JWT signature, issuer, audience and lifetime checks reuse **Microsoft.IdentityModel.JsonWebTokens 8.23.0**, repository commit `8b16f418b3d587416d293e9ba34c5d310781cdb3`. HTTP/TLS, loopback HTTP serving, random bytes, SHA-256, fixed time comparison and JSON are BCL components. System protection belongs to [Storage](../Storage/README.md).

Signed protocol fixtures and storage tests do not prove a real account's model access. Login URLs are returned to the GUI that explicitly requested login. Model reasoning, execution and skill optimization belong outside this module.

**Do not reinvent infrastructure.** Reuse the existing JWT library, HTTP/TLS and vault components. Another vendor's login needs a supported public contract, provenance and contract tests; translating open source code does not authorize private credentials or a disguised official client.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](IChatGptAccountService.cs)
- [Original reference 2](ChatGptAccountService.cs)
