# Command sessions provenance

Reviewed 2026-10-30 against OpenAI Codex commit [`788b9d7bf6e37f735647b7f84b556c2a807df4d8`](https://github.com/openai/codex/tree/788b9d7bf6e37f735647b7f84b556c2a807df4d8), committed 2026-10-30T07:11:56Z.

`CommandSessions.cs` adapts the **behavioral contract** of Codex unified exec: a command can yield an owned identifier before it finishes; subsequent calls collect bounded output and distinguish a live process from a terminal result. It does not copy or embed Codex's Rust process runtime, launch the Codex executable, or delegate agent execution to Codex.

The [official Codex prompting guide](https://developers.openai.com/cookbook/examples/gpt-5/codex_prompting_guide) describes `exec_command` for long-lived command sessions and `write_stdin` for input or output polling. The implementation references below were fetched at the fixed commit, rather than inferred from tool names.

## Source mapping

| Codex source and original names | Behavior adapted in Cogito |
| --- | --- |
| [`core/src/unified_exec/mod.rs`](https://github.com/openai/codex/blob/788b9d7bf6e37f735647b7f84b556c2a807df4d8/codex-rs/core/src/unified_exec/mod.rs): `UnifiedExecProcessManager`, `ProcessStore`, `ProcessEntry`, yield/output limits | A registry owns active commands, bounded starts return a command ID, and terminal status is distinct from a yielded running result. |
| [`core/src/unified_exec/process_manager.rs`](https://github.com/openai/codex/blob/788b9d7bf6e37f735647b7f84b556c2a807df4d8/codex-rs/core/src/unified_exec/process_manager.rs): `allocate_process_id`, `exec_command` / `exec_command_inner`, `write_stdin` / `write_stdin_inner`, `refresh_process_state`, `collect_output_until_deadline` | `start_command` returns promptly; `read_command` polls a known owned ID and returns bounded output, running status, and an exit code only when observed. Unknown or ownership-lost commands are not silently relaunched. |
| Same manager: `terminate_process`, `terminate_all_processes`, `release_process`, `InitialExecCommandGuard` | Owned execution resources have explicit shutdown paths; failure and cancellation must not be mistaken for successful completion. |
| [`core/src/unified_exec/process.rs`](https://github.com/openai/codex/blob/788b9d7bf6e37f735647b7f84b556c2a807df4d8/codex-rs/core/src/unified_exec/process.rs): `OutputBuffers`, `OutputHandles`, `UnifiedExecProcess::has_exited`, `exit_code`, `terminate`, `terminate_confirmed`, and `Drop` | Separate output retention from process liveness, cap retained output, and perform lifecycle cleanup. |
| [`core/src/unified_exec/output.rs`](https://github.com/openai/codex/blob/788b9d7bf6e37f735647b7f84b556c2a807df4d8/codex-rs/core/src/unified_exec/output.rs) | Preserve explicit information about omitted/truncated output instead of presenting a bounded excerpt as the whole transcript. |

This is a focused behavior adaptation, not a line-for-line module translation. Codex's PTY implementation, exec-server transport, sandbox/network approval machinery, signal handling, retention policies, pruning and tool schemas are not included. The 2026-10-31 extension adds a `write_command` input contract corresponding to the referenced `write_stdin` behavior, while reusing .NET streams and Porta.Pty rather than porting Codex's runtime.

## Independent .NET ownership and storage implementation

- .NET `Process`, redirected streams, cancellation tokens, `SemaphoreSlim`, and `ConcurrentDictionary` supply execution and coordination. Actual terminals reuse the existing Desktop dependency Porta.Pty 2.2.3, package source revision [`0186b604bf40161f199823fe5cdb2dfa02c37bea`](https://github.com/tomlm/Porta.Pty/tree/0186b604bf40161f199823fe5cdb2dfa02c37bea). Its MIT license is retained as `Porta-Pty-LICENSE.txt`; no native interop or Rust runtime was ported.
- Each command uses a hidden worker instance of the existing Cogito Host. The launch envelope arrives on stdin; parent-pipe EOF cancels the command. This parent-loss mechanism is Cogito's independent implementation, not a copied Codex algorithm.
- Commands are noninteractive by default. Explicit `interactive` enables bounded stdin messages; optional `tty` selects Porta.Pty's actual terminal. The existing worker ownership pipe carries JSON input messages and bounded acknowledgements, rather than adding an IPC server. Pipe `closeInput` provides actual EOF; terminal half-close is explicitly unsupported. `read_command` uses UTF-8 byte cursors over bounded retained logs; record writes use atomic single-file replacement.
- Ownership checks bind logs to the chat and workspace. Later turns in that chat may inspect retained logs, but process IDs from an interrupted Host are never reattached or automatically replayed.
- Default still-running commands are stopped at run cancellation/end. Explicit session-owned development commands may survive normal completion until their fixed expiry, chat cleanup, stop or Host loss; cancellation still applies. A running command ID is never equivalent to a zero exit status, and a zero exit status alone is not proof that the user's task was solved.
- This does not provide an OS sandbox, terminal emulator, cross-Host process restoration, or containment of arbitrary deliberately detached descendants. Real Windows/Linux/macOS process behavior requires testing on those operating systems.

## Notices

Codex's original [Apache-2.0 LICENSE](https://github.com/openai/codex/blob/788b9d7bf6e37f735647b7f84b556c2a807df4d8/LICENSE) and [NOTICE](https://github.com/openai/codex/blob/788b9d7bf6e37f735647b7f84b556c2a807df4d8/NOTICE) are retained verbatim as `Codex-LICENSE.txt` and `Codex-NOTICE.txt` for attribution of the behavioral reference. The upstream NOTICE also mentions Ratatui; retaining that exact notice does not mean this .NET command subsystem incorporates Ratatui.

SHA256 of the downloaded original notice files:

- LICENSE: `d17f227e4df5da1600391338865ce0f3055211760a36688f816941d58232d8dc`
- NOTICE: `9d71575ecfd9a843fc1677b0efb08053c6ba9fd686a0de1a6f5382fd3c220915`
