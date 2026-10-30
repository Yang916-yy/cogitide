# Long tasks, large workspaces and remote development

Engineering round: **2026-10-31**. This record describes implemented behavior and its measured acceptance scope. The [previous maturity assessment](../README.md) and older dated reports are not updated retroactively. No paid model or personal SSH endpoint is used by this round's fixtures.

## Durable task execution

Default run budget is **256 model iterations / six hours**, configurable through the desktop Task budget form up to **10,000 iterations / seven days**. Context budgets remain explicitly measured in characters (4,000–1,000,000), not exact tokenizer counts. An optional reported input-plus-output token limit is shared by normal inference and compaction. Missing/incomplete usage remains explicit; enabling a token cap pauses on unavailable usage. A call already in flight can exceed its remaining reported budget: this is a between-call control, not a billing hard limit or a USD estimate.

Time, iteration and token-budget stops produce `run.paused` with a durable checkpoint. The original task boundary, instructions, observed progress, usage and accepted steering watermark are retained. Desktop offers **Continue task**; `run.continue` uses a fresh, explicit budget while inheriting omitted top-level options, including permissions and research settings. Supplied research objects replace that object. Completed or malformed checkpoints are rejected before mutation. Unsupported continuation attachments are rejected rather than discarded.

Host acceptance follows the durable `run.accepted` record, before external tool initialization or inference. A request that fails before acceptance cannot turn the prior task into a failed task. Workspace ownership remains held through cancellation/cleanup. Stored tool proposals are context, never an execution queue: unknown results are closed explicitly, project rules and credentials are rechecked, and old commands are not replayed.

Queued input acceptance and application use a persisted sequence watermark saved with messages. Legacy queued input without a trustworthy watermark is review-only: review the recovered instructions and start a new chat explicitly. Hard Host loss marks interrupted runs on restart; it does not reattach old process IDs or infer successful actions.

The implementation reuses the existing AgentEngine/SQLite store, transactions, C# records, source-generated System.Text.Json, CancellationToken and existing SDK streams. It adds no scheduler service, database, model transport or second agent loop. [Runtime](../src/Workbench.Core/Runtime/README.md), [persistence](../src/Workbench.Core/Persistence/README.md), [Host](../src/Workbench.Host/README.md).

## Bounded history and large-project presentation

Desktop opens `session.view` and keyset-paged `session.events.page`, with earlier messages through `session.messages.page`. SQLite projects a bounded selection rather than returning the complete conversation/journal to the UI. Pages preserve whole tool groups and stable source positions; oversized indivisible pages report unavailable without advancing/skipping their cursor. Display views omit binary attachment payloads and private provider continuation, with an explicit omission marker; original storage/model input is unchanged. Pending instructions have a separate bounded recovery projection, so a recent event page does not falsely imply no older accepted input.

The timeline uses Avalonia virtualization and a rolling maximum of **1,000 presentation entries**. Earlier durable history stays accessible. File filtering runs on the Host over the existing Git inventory; a directory prefix narrows the actual inventory before its cap. File lists are snapshot-bound and previews use the existing full-file SHA-256 and long-line cursors. UI shows page positions and omitted-inventory status rather than silently treating the first 2,000 paths or first file megabyte as the whole project.

Evidence recall uses indexed exact-event reads. Evidence ledger replay and project-experience scanning use bounded, high-watermark streams which release the SQLite gate before yielding. No private-source cache/index or replacement storage engine is introduced.

Remaining bounds are deliberate and visible: Git inventory is capped at 20,000 selected paths; content search at 2,000 files/32 MiB; semantic analysis is bounded source-only Roslyn. Complete model snapshots still use the original full Load/save path, and SQLite JSON projection can still parse the stored snapshot. This is improved returned/managed history scope, not a claim of constant memory or constant work for every session operation. Full project builds/LSP and million-file indexing are not implied.

## Development commands and isolated worktrees

`start_command` supports controlled `interactive=true` pipe input or `tty=true` real PTY input through existing **Porta.Pty 2.2.3**. `write_command` is approval-required, bounded, acknowledged and owned by chat/workspace. Pipe mode can close stdin; a PTY has no portable half-close and rejects that request. Input backpressure has deadlines; lost acknowledgments are unknown outcomes and never automatic retries.

Default command lifetime remains the current run. Explicit `lifetime="session"` supports development servers across ordinary run completion, bounded by expiry. Explicit chat stop and Host loss stop owned jobs; a later run can inspect their status/logs and send approved input. Live/unfinished status never becomes successful verification. Output remains bounded and uses the existing UTF-8 byte cursors.

`create_worktree`, `list_worktrees`, `worktree_status`, and `remove_worktree` reuse Git's native operations. A GUID ownership record binds managed sibling paths to the creating chat and repository. Creation/removal require approval; listing/status are read-only. No force removal, automatic branch deletion, automatic merge or custom recursive-delete engine is introduced. Dirty, untracked and ignored user data block removal. Path/link/repository identity checks precede mutation, inherited Git redirects are removed by the shared process helper, optional index refresh is disabled, and implicit post-checkout hooks are disabled for creation. Checkout filters remain Git behavior; commands/worktrees are not an OS sandbox.

See [command lifecycle and provenance](../src/Workbench.Tools/Execution/README.md) and [managed worktrees](../src/Workbench.Tools/Worktrees/README.md). The existing explicitly opened user terminal remains local; model PTYs run on the selected local/remote Host.

## WSL and SSH connections

The desktop Host connections form stores nonsecret local/WSL/SSH profiles. **Host, model requests, workspace files, tools, MCP and credentials live on the selected endpoint**. Switching connections recreates the owned client and clears workspace/session/account presentation caches. POSIX paths are validated on the Host, not normalized with the local Windows filesystem.

WSL starts a matching preinstalled Linux Host with system `wsl.exe --distribution … --exec …`. SSH uses system OpenSSH configuration/keys/agent/known_hosts, with noninteractive authentication and strict host-key checking. There is no SSH protocol port, extra HTTP listener, automatic key acceptance, personal account-file migration or remote installer. Current/recent API-key variable names, `API_KEY`, `*_API_KEY`, and matching WSLENV forwarding entries are removed from transport-child environments. Explicit trusted SSH `SetEnv` literals remain user configuration outside that filtering boundary.

Remote startup verifies wire version and current capabilities. SSH command arguments use POSIX quoting; supported SSH accounts need a POSIX-compatible shell. Normal connection shutdown closes the owned Host stdin and stops its resources. A network partition can delay delivery/confirmation; inspect remote state after reconnect. Offline continuation is not a supported feature of this transport. Reconnect reads retained state and explicitly continues a stopped task. Remote loopback OAuth callbacks are not tunneled automatically; authenticate through a supported device flow or directly on the endpoint.

Publish the complete matching headless package with:

```powershell
./scripts/publish.ps1 -Runtime linux-x64 -HostOnly -OutputDirectory artifacts/host-linux-x64
python scripts/verify-release.py artifacts/host-linux-x64 --require-source-locks --require-unified-kernel --require-tool-upgrade
```

The normal publishing path supplies separate RID locks, pinned runtime, component/license metadata and SHA256SUMS. No Avalonia Desktop is required on the endpoint. Choose the matching architecture, copy the complete directory and set executable permissions on Unix. [Connection setup and validation commands](../src/Workbench.Desktop/Remote/README.md).

## Sources and implementation boundary

[Codex long-horizon guidance](https://developers.openai.com/blog/run-long-horizon-tasks-with-codex) motivates persistent specifications/progress and repeated verification; its documentation does not establish universal numeric run limits. The defaults here are Cogitide engineering choices. [Official remote connections](https://learn.chatgpt.com/docs/remote-connections) describe endpoint-local execution/authentication through SSH. [OpenCode WSL](https://opencode.ai/docs/windows-wsl/) and [server](https://opencode.ai/docs/server/) document separating the desktop/client from endpoint execution. This round references those behaviors; it does not copy their server/frontend code or introduce their HTTP protocol into the existing stdio Host.

Git, SQLite, Avalonia, native OpenSSH/WSL and Porta.Pty own mature infrastructure. Fixed existing command behavior references and Porta's MIT notice remain in [Commands provenance](../src/Workbench.Tools/Licenses/Commands/PROVENANCE.md). **Do not reinvent unnecessary infrastructure** in these adapters. Any replacement requires an actual missing capability, compatible resource/permission boundaries and targeted failure evidence.

## Verification record

- The feature validation below belongs to commit `4ea69b3016616982a68b9a867d9fbf3385730732`. Locked dependency restore and strict Debug/Release builds passed with zero warnings/errors. Release suite: **639 passed, zero failed, one intentionally gated real-remote test skipped**. Debug focused suite: 67 passed plus the same gate.
- The gated actual Desktop HostClient test was separately enabled against the **final Linux package** and passed once through WSL and once through real system OpenSSH. It initialized, read snapshot-bound files, executed explicit generated commands, retained isolated sessions, closed and reconnected. No GUI or personal endpoint was used.
- All seven local Host fixtures passed: host, lifecycle, Responses, continuity, paged tools/background ownership, research mechanisms, and MCP/headless-browser/Roslyn integration. Current Host catalog has 24 base/knowledge tools, 15 under ReadOnly; configured MCP remains additional and approval-gated.
- Real WSL and isolated OpenSSH→AsyncSSH→WSL probes passed Unicode/apostrophe paths, inventory/file paging and hashes, explicit command output, stdin-EOF cleanup before a delayed child write, persistence and no replay after restart. OpenSSH rejected both a wrong generated host pin and wrong generated client key. The final loopback probe verified cleanup without RuntimeWarning. AsyncSSH is a pinned optional **test-only** server, not a production dependency.
- Final headless Linux-x64 package: **426 files**, native headers/runtime verified, six matching production source locks, model/tool/license/root notices verified. WSL/native SSH used this package; both actual client acceptance tests had zero skips. The regular suite remains opt-in rather than contacting arbitrary endpoints.
- New three-platform CI also publishes a matching headless Host and runs its native no-model smoke. Its final result belongs to the run associated with the published commit, independently of these Windows/WSL measurements.

The [first three-platform run](https://github.com/Yang916-yy/cogitide/actions/runs/41097027475) retained three portability findings. The follow-up normalizes only macOS's verified system `/var`, `/tmp` and `/etc` aliases to their expected `/private` targets, while still rejecting custom ancestor links. Publishing explicitly handles hidden Unix files both when removing the incomplete marker and when hashing the complete package. The Windows fake-transport test now waits for the exact EOF marker payload instead of racing file creation against its write; no production remote behavior changed. A new macOS-only regression checks alias/physical-path ownership and custom-link rejection on the actual platform. Follow-up results and hashes are recorded separately from the original feature measurements.

The [portable validation record](long-tasks-remote-validation.json) includes source/assembly/evidence hashes, counts, scope and retained negative findings. Raw runtime state, keys, reports, SDKs and package binaries remain ignored under artifacts/.

Functional fixtures and native transport acceptance do not establish real-model coding solve rate, research benefit, complete GUI certification or compatibility with every SSH server. Those remain separate evaluations.

## 中文入口

本轮完善长任务预算与显式续接、分页历史及文件浏览、交互式命令/PTY、受控 Git 工作树，并接入系统 WSL/OpenSSH。远程凭据与执行留在对应 Host，不复制本机账号。断开连接会停止本次启动的 Host，之后可以读取状态并显式续接，不宣称离线继续。实际验收使用独立 WSL 测试目录及临时 SSH 测试端，无个人远端、付费模型或桌面接管。
