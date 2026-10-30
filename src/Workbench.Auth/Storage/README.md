**English** | [简体中文](README.zh-CN.md)

# System protected credential storage

`ProtectedAuthStore` stores this application's own registrations, account credentials and nonsecret metadata, with installation isolation, an exclusive lifetime lock and complete token set commits. User conversations and code history remain in [Core/Persistence](../../Workbench.Core/Persistence/README.md); they are not merged into an unprotected JSON account file.

Native protection reuses the public `Storage` API from **Microsoft.Identity.Client.Extensions.Msal 4.92.0**, fixed commit `2465fc31fa2b481b68660670e2becfd3fba7c631`: Windows DPAPI, macOS Keychain and Linux libsecret/Secret Service. Windows `ProtectedData` is explicitly pinned to **10.0.14**. MSAL supplies storage, not Microsoft account authentication. The component's missing cross process lock is supplied by an exclusive BCL file handle. Vault identifiers include the application data directory; Windows encrypts a temporary file before an atomic rename. Exact licenses and platform limits are in [Auth provenance](../Licenses/PROVENANCE.md).

An unavailable native vault fails explicitly without plaintext fallback. Cross platform compilation does not verify the vault on every OS.

**Do not reinvent infrastructure.** Do not add a credential daemon, cryptography, Keychain/libsecret wrapper or plaintext fallback. A new storage component needs a demonstrated platform gap, system evidence, mature alternatives and migration/corruption recovery tests.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](ProtectedAuthStore.cs)
