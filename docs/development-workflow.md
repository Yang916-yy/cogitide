# Coding workflow and acceptance boundaries

Cogitide's Desktop, stdio Host and detached remote Host use the same agent kernel. This guide describes the implemented workflow and how to verify it. It is not a test-result record; current results and platform qualifications must come from the corresponding validation run. Existing dated [integration](integrations-performance.md), [long-task/remote](long-tasks-remote.md) and [release](release.md) records retain their historical scope.

The [2026-11-02 validation record](development-workflow-validation-2026-11-02.md) reports the current local fixture and platform acceptance scope.

## Connect a provider

A fresh installation asks for a ChatGPT account or API connection and starts without a selected provider, endpoint or model. Existing settings retain their configured provider/authentication choices. Account login authenticates the same kernel; it never starts another coding agent.

The API connection UI accepts a masked key and saves it in the selected Host's protected native vault: Windows DPAPI, macOS Keychain or Linux Secret Service. Settings hold a nonsecret credential reference, and a vault failure is an error without a plaintext fallback. Existing environment-variable authentication remains supported. Remote Hosts own separate credentials; connecting or deploying does not transfer a saved local key.

OpenAI, Anthropic, DeepSeek, OpenRouter and Mistral presets offer address-based hints, alongside custom endpoints and protocol selection. Keys do not identify a vendor or model. Enter a model ID manually or explicitly select **Fetch models**. Catalog discovery makes bounded metadata GET requests, never inference or a model benchmark; unknown capabilities remain unknown. A listing cannot establish entitlement, tool compatibility or image support. See the [provider setup contract](../src/Workbench.Core/Models/PROVIDER-CONFIGURATION.md).

## Navigate and edit code

Text search, checked reads, syntax outlines and source-only Roslyn navigation remain available without a language server. Roslyn does not evaluate project builds. For richer language-specific queries, configure the endpoint-local Host's `lsp.json` using existing absolute server executable paths. Supported IDs cover TypeScript/JavaScript, Python, C#, Go and Rust. No server is downloaded or automatically selected from repository configuration.

Each LSP root needs an explicit trusted-workspace grant. Loading configuration does not launch a server; the first approved `lsp_query` starts it. Servers can load plugins, inspect project configuration and invoke build tools with the granted authority. ReadOnly mode excludes these external-server tools. The adapter exposes definitions, references, hover, file diagnostics and document symbols when advertised by the server; it rejects server-requested edits and exposes no execute-command tool.

Queries use bounded Git-aware source/configuration snapshots, checked hashes, one-based UTF-16 positions and explicit truncation/continuation metadata. An observed empty diagnostic list differs from no publication; ignored/generated files, external dependencies and the server's entire state are outside the snapshot. TypeScript/Python real-server fixtures are opt-in; the shared adapter is not evidence of acceptance against every C#/Go/Rust server. See [configuration, upstream references and limits](../src/Workbench.Tools/Lsp/README.md).

## Review and publish deliberately

Open **Review changes** for the selected repository. Inspect working and staged areas, select a file or text hunk, and stage/unstage the selected change. Discard restores working changes from the index and requires concrete confirmation; it preserves staged content. Binary/mode/add/delete changes use file actions. Conflicts and unsupported paths require ordinary Git inspection.

Review state binds HEAD, branch, the complete index delta and checked working changes. A stale action fails and requires refresh. Scoped subdirectories can review/stage their paths; commit and publication require the repository root so the complete staged content is visible. Stop active work before review mutations. Snapshot-anchored comments add path/hunk/range context to the draft prompt; adding feedback does not send a task.

**Commit** uses exactly the reviewed staged index and an explicit message without implicit staging. This review path disables hooks and signing; use normal Git when a project needs those steps. **Push** is a separate confirmation to the configured upstream without force or configuration changes. **Create pull request** separately invokes installed `gh` with the existing branch and its existing authentication; it does not automatically push. An unknown timeout outcome requires inspection before retry. See [Git command reuse and safeguards](../src/Workbench.Tools/GitReview/README.md).

## Require native tool isolation

Ordinary execution modes control application permissions and approval. The separate **Require OS isolation** option runs workspace tools in a native MXC-contained worker. If an asset/prerequisite is unavailable, policy validation fails or native launch reports a warning, the requirement fails closed. It never falls back to an ordinary tool process.

The worker receives the selected workspace, private scratch, bounded runtime/system grants and an explicit environment allowlist. Host credentials/state and common credential directories are denied; host loopback and IP ingress/egress are denied. ReadOnly mode also grants the workspace read-only at the native policy layer. Approved writes inside a writable workspace retain their ordinary authority. External LSP/MCP integrations run only within those grants and network restrictions; browser checks can report denied network access. Worktree administration is omitted because sibling directories fall outside this policy.

The pinned Microsoft.Mxc.Sdk 1.0.1 includes native assets for Windows x64/ARM64, Linux x64/ARM64 and macOS ARM64. It has **no Intel macOS (`osx-x64`) asset**. The Intel macOS application package remains a valid target, with required OS tool isolation unavailable. Asset presence or advisory discovery is not native enforcement acceptance. Backend prerequisites and cleanup strength differ; macOS process-group termination does not prove every deliberately detached hostile descendant exited. See [native policy, ownership and platform limits](../src/Workbench.Tools/Isolation/README.md).

## Deploy and reconnect to WSL or SSH

Install the WSL distribution or configure trusted SSH key/agent access outside Cogitide. Verify SSH fingerprints independently; the application never accepts changed keys, installs an SSH server or edits personal SSH configuration. Native Windows is a local Host target; guided remote service startup expects a POSIX endpoint.

1. Build a complete self-contained Host publish for the endpoint RID from the same source/build/configuration as Desktop, then verify its manifest. For example, `./scripts/publish.ps1 -Runtime linux-x64 -HostOnly -OutputDirectory artifacts/host-linux-x64` followed by `python scripts/verify-release.py artifacts/host-linux-x64 --require-source-locks --require-unified-kernel --require-development-workflow`.
2. In **Host connections**, create/select the SSH or WSL profile and open the explicit deployment window. Choose that prebuilt package and new absolute installation/data paths. The guided flow transfers and verifies it, starts the durable service and records its identity. Prior manual Host deployment is optional. Existing endpoint paths are refused, and partial failure state is retained for inspection.
3. Connect to the durable service and open a project by its endpoint-local path. Endpoint credentials, Git/language toolchains, LSP/MCP configuration and browser prerequisites remain endpoint-local. A self-contained publish bundles both .NET and ASP.NET Core; manual framework-dependent deployment needs both matching shared frameworks.
4. Reconnect after desktop or transport loss to reload original SQLite pages, live pending approvals and notifications. The bridge never replays actions or infers success from an unanswered request. An approval reached while disconnected stays pending until an explicit response, cancellation or budget/service stop.

Only **durable service mode** keeps accepted work running after attachment loss. Foreground stdio mode stops the Host/owned resources on observed EOF. Service crash, reboot, explicit shutdown and cancellation remain stopping events; restart marks interrupted work and requires explicit continuation. Detached startup is a user process, not boot-time service installation. Package integrity checks do not authenticate a publisher, and a same-user process that can read the private endpoint descriptor is outside the loopback authentication boundary. See [remote setup](../src/Workbench.Desktop/Remote/README.md) and [durable service design](../src/Workbench.Host/RemoteService/README.md).

## Reuse and provenance

| Capability | Mature component and project-specific adapter |
| --- | --- |
| LSP / worker RPC | StreamJsonRpc 2.26.150 owns framing, parsing, dispatch and cancellation. The [bounded PipeReader decorator](../src/Workbench.Tools/Transport/README.md) supplies an input-buffer rejection policy absent from that handler. OpenCode's fixed revision is a behavior reference, with no copied source. |
| Native containment | Microsoft.Mxc.Sdk 1.0.1 owns native policy validation/launch/enforcement. Cogitide supplies grants, restricted RPC DTOs and lifecycle integration; the package does not publish a source commit. |
| Review | Installed Git owns patch semantics, index locks, commit and transport; `gh` owns PR creation. A small display-boundary matcher preserves Git-generated hunk bytes and delegates validation/application back to Git. |
| Durable remote transport | ASP.NET Core Kestrel, BCL WebSockets/Channels/HttpClient and existing SQLite/Host contracts; OpenSSH/WSL authenticate endpoint access. No second agent, HTTP parser or event store is introduced. |
| Deployment / provider setup | BCL TarFile/hash/process primitives and existing release manifests; existing pooled HTTP transport and native MSAL storage. No SDK bootstrap/download, key forwarding or plaintext credential store is added to these flows. |

[Third-party notices](../THIRD_PARTY_NOTICES.md), fixed module references and package locks document provenance. These engineering integrations make no measured research-benefit claim.

## Acceptance before release

Run the pinned-SDK build/tests in the background and serialize commands sharing `obj/`. Run the existing Host integration fixtures and `python scripts/module-map.py --check` before publication. No paid provider or real desktop is needed for deterministic acceptance.

Check the fixture families separately: `ProviderSetupTests`/`ProviderSetupUiTests` for explicit setup, metadata and vault boundaries; `LspIntegrationTests`/`BoundedRpcPipesTests` for RPC/query bounds and cleanup; `GitReviewTests`/`ReviewUiTests` for real Git snapshots and user-action separation; `RemoteDeploymentTests`/`DurableRemoteServiceTests`/recovery tests for deployment integrity, disconnect ownership and replay-free recovery; isolation policy/worker tests for configuration and lifecycle.

Opt-in native denial tests (`COGITIDE_TEST_NATIVE_ISOLATION=1`), real TypeScript/Python servers, and real WSL/OpenSSH tests must report the actual platform/server/backend and skips. A successful ordinary worker or headless window test does not establish OS isolation or visual GUI acceptance. Release verification's `--require-development-workflow` checks packaged SDK versions, MIT/license notices, available native assets and ASP.NET Core dependencies; it does not execute a sandbox, deploy remotely or certify a GUI. Record actual command results and remaining gaps in a dated validation report rather than treating these instructions as passed evidence.
