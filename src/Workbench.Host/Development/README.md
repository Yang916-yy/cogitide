# Desktop development and provider RPCs

These partial Host adapters expose native Git review and provider configuration to the Desktop over the existing authenticated local/remote transport. They do not implement another agent kernel or model gateway.

`WorkspaceIntegrations` reuses the existing workspace lease dictionaries and a BCL admission lock, command lifetime manager, LSP/MCP adapters and native-isolation broker. Review mutations exclude agent runs and workspace commands; Git's own index locks and snapshot validation remain authoritative. [Git review provenance](../../Workbench.Tools/GitReview/README.md) records the actual upstream behavior references and reused native Git operations.

`ProviderConfigurationRpc` delegates to the existing pooled `SocketsHttpHandler` transport, official model adapters and native MSAL credential storage. Settings contain endpoint/protocol-bound profile references rather than keys. [Provider configuration](../../Workbench.Core/Models/PROVIDER-CONFIGURATION.md) describes explicit metadata discovery, address hints, vault failure behavior and verification limits.

This module is application glue, not a source-code port. Do not replace the mature Git, HTTP, credential-storage, JSON or process components with parallel custom infrastructure. New infrastructure needs a concrete missing capability, comparison and meaningful validation under the repository rules.
