**English** | [简体中文](README.zh-CN.md)

# Official account authentication

[Accounts](Accounts/README.md) integrates the documented OAuth account contract; [Storage](Storage/README.md) protects credentials with system facilities. Login supplies model authentication only. Agent execution stays in the shared Core kernel, and this library does not read another application's account files.

The public name is Cogitide; historical system storage identifiers remain compatible. Sources, exact dependency commits and unmodified notices are recorded in [Auth provenance](Licenses/PROVENANCE.md). The implementation reuses BCL HTTP/TLS/JSON and Microsoft JWT and native credential storage components. Real authorization and each platform's vault require separate live verification; local protocol fixtures do not establish account entitlement.

**Do not reinvent infrastructure.** Reuse mature .NET cryptography, JWT validation and system vaults. A vendor without a supported public login contract uses its API integration; do not invent private subscription authentication.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.
