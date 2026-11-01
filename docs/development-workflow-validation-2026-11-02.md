# Development workflow validation — 2026-11-02

This record covers the coding workflow, provider setup, native tool isolation and durable remote service changes developed after `17474b9`. It records functional evidence rather than model task-success, security certification or research-benefit measurements. Tests ran in the background without opening the Desktop or calling a paid provider.

## Implemented behavior

- Fresh Desktop settings require an explicit account/API and model choice. Connections accepts masked API keys, stores endpoint/protocol-bound references through native MSAL storage, supports key rotation and retains configured environment variables. Address hints cover OpenAI, Anthropic, DeepSeek, OpenRouter and Mistral; custom endpoints remain available. Explicit model discovery performs metadata GETs. A catalog does not prove entitlement or tool/image compatibility.
- Explicitly trusted endpoint-local LSP configuration adds definitions, references, typed hover, diagnostics and document symbols. Microsoft StreamJsonRpc owns the protocol; cancellation has independent local deadlines. Windows encoded-drive file URIs are normalized before workspace checks. Recommended TypeScript semantic configuration disables its optional syntax-only server.
- Native Git provides snapshot-bound file/hunk staging, unstaging and confirmed restore, staged-only commits, configured-upstream push and explicit `gh` PR creation. Inline feedback enters the composer as an unsent draft. Missing replies remain unknown outcomes. Tests publish only to generated local bare repositories.
- A native MXC worker executes workspace tools with separate filesystem/network/process/UI restrictions. The trusted Host retains model credentials and the shared kernel. Rule/skill/experience file bytes traverse the exact worker owner; internal evidence storage remains outside it. Required isolation never falls back to an ordinary process.
- The optional remote service retains accepted work after SSH/WSL attachment loss. Reconnect uses coherent SQLite message/event cursors, bounded forward replay and pending approvals; actions are never automatically replayed. Guided deployment verifies a complete matching package, transfers it to fresh directories and starts a pinned per-user service.

## Observed checks

The initial complete Debug run passed **737 tests**, failed none and conditionally skipped seven. Subsequent fixes and additional fixtures are covered by the final run recorded below; this initial count is retained as historical evidence rather than substituted for that final run.

The final strict **Release** build passed with zero warnings/errors. Its complete run passed **749 tests**, failed none and skipped four out of **753**, with real TypeScript/Python and native Windows isolation explicitly enabled. The four skips were the three separately opted-in remote acceptance tests and the macOS-only alias fixture. Actual remote acceptance is recorded separately below.

The seven local Host integration fixtures passed: ordinary coding/approval/persistence; lifecycle/concurrency; unified Responses; repeated compaction and continuity; paged tools/background ownership; explicit research mechanisms; MCP/browser/Roslyn. These used generated files and loopback model fixtures. No external model task-success or paid benchmark claim follows.

Actual TypeScript 5.9.3 with typescript-language-server 5.1.3 and Pyright 1.1.414 passed cross-file definition/reference, typed hover, symbol and error-diagnostic acceptance under Node 22.20.0. The servers were installed only under ignored `.tools/lsp-servers`; tests do not install software or start arbitrary repository servers.

On this Windows host, both real native ProcessContainer fixtures and five policy fixtures passed (**7/7**). They verified allowed workspace writes and byte-identical rule reads alongside denied outside-file read/write, protected Host-state access, host-loopback TCP, parent-fixture termination, inherited credentials and clipboard access. The clipboard fixture attempts only opening the clipboard, requires access-denied and never reads or changes clipboard contents. It also verified native read-only grants, exact owner checks, session-command retention, cancellation and incomplete-cleanup accounting.

The CLR startup fix uses explicit SDK Host-UI restrictions while allowing the runtime's imported UI subsystem. A private controlled probe reproduced the default Win32k-disable failure and verified the compatible setting before the actual denial suite. [Upstream issue 1622](https://github.com/microsoft/mxc/issues/1622) records the matching CoreCLR failure. The policy does not claim Win32k syscall denial; [its module documentation](../src/Workbench.Tools/Isolation/README.md) records that boundary.

Both localization resources contain **408 matching keys**. Headless compiled-window fixtures cover provider setup, review controls, live translation, remote restoration and isolation-policy continuation. Native GUI acceptance is separate.

## Corrections exposed by real acceptance

An uncooperative language server showed that JSON-RPC cancellation alone is advisory. Local deadlines now reach owned-process cleanup; regression fixtures cover ignored cancellation and shutdown. Real server tests exposed Windows percent-encoded drive URIs and TypeScript syntax-only startup behavior, rather than accepting empty or alias-only results.

WSL mounted Windows directories report permissions unsuitable for private service discovery. Real daemon fixtures use generated private Linux-native directories while preserving the rejection of insecure state. Self-contained POSIX apphosts must match the complete filename `Workbench.Host`; stripping `.Host` incorrectly requested an uninstalled `dotnet` executable. Actual service fixtures cover that launch path.

Cross-RID Host DLL identity originally differed because of compiler intermediate/PDB paths and inferred PE architecture. Host-only .NET compiler `PathMap` and `PlatformTarget=AnyCPU` normalize those differences; the native apphost/runtime still follows its RID. The final tested matching Debug Host hash was `1427091ab83eaa19d7692af66f8347ea721eca15a94df62bc66e87fa930b5e53`. Guided deployment retains exact Host/Protocol hashes and the full package manifest; it uses no source-only or version-only executable fallback. Hash integrity does not authenticate a publisher.

Actual WSL acceptance passed **9/9** in the focused final run: six Kestrel/bridge/Host fixtures, unexpected clean-EOF presentation, guided package transfer/start/reattachment/existing-path refusal, and native durable-service lifecycle. The lifecycle test verifies exactly one delayed write after attachment loss, explicit service shutdown, notification of disconnect and absence of a stopped child's later write. It uses actual Desktop HostClient instances and the self-contained Linux Host, not a simulated transport.

The corresponding foreground HostClient and durable-service lifecycle tests also passed through actual system OpenSSH and a generated AsyncSSH 2.24.1 loopback server backed by WSL. They ran sequentially against the generated workspace. The protocol fixture additionally rejected wrong generated host/client keys. No personal SSH configuration, keys, remote server or real model account was accessed.

Service shutdown also exposed non-cancellable console wrappers and missing notification on clean EOF. The bridge CLI now uses owned asynchronous BCL pipe readers/writers with independent local waits; completed frames explicitly flush, while teardown does not flush abandoned work. A deterministic uncooperative-reader regression and actual WSL pipes cover that correction. An unexpected clean Host EOF now signals the Desktop; an unanswered Git publication remains an explicit unknown outcome with a client deadline exceeding its server operation budget.

The first macOS CI run exposed its 104-byte Unix socket-path limit in the in-process worker RPC test's long named-pipe prefix. The fixture now uses a short prefix while retaining a full random GUID. Production worker transport remains standard input/output; no sandbox or runtime behavior was weakened for this fixture correction.

## Platform and product limits

Windows native enforcement above is actual local acceptance. Linux/macOS native-denial acceptance has not been performed here. The pinned MXC 1.0.1 package has no Intel macOS native asset. Deliberately detached macOS descendants retain Seatbelt restrictions, but process-group termination is not proof they all exited. External toolchains outside explicit grants fail rather than widening the policy, and isolated browser/network operations remain denied.

WSL and generated loopback OpenSSH acceptance do not certify every public SSH server or distribution. Durable mode is a per-user service; reboot/crash recovery requires explicit continuation. Foreground mode retains EOF shutdown. No personal remote server, credential migration, real GitHub PR, native macOS/Linux GUI, signing, notarization or performance benchmark is claimed.

See [workflow and provenance](development-workflow.md) for configuration, component reuse and upstream references. Raw local fixtures, tokens, SDKs and runtime databases remain outside Git.
