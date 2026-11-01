# Cogitide remote Host package

This is the headless Host, without the desktop UI. Keep the **complete directory** together. Use a package matching the endpoint operating system and architecture. `publish-info.json` records the pinned runtime and package kind; `SHA256SUMS` detects changed files and is not a publisher signature.

On Linux/macOS, allow the native Host to execute:

```sh
chmod u+x Workbench.Host
```

The desktop can launch this executable through system OpenSSH or WSL with `--stdio`, or attach to its durable service through the same authenticated transport. Set its **absolute endpoint path** in a saved Host connection. The guided deployment action can transfer a matching complete prebuilt package to a new private endpoint directory, validate its manifest and start the durable service; it does not install an SDK or configure SSH. An optional data directory must also be an absolute endpoint path. Keep that state separate from source, the package directory, and any other concurrently running Host. Stdio stdout is the JSON protocol; do not add banners or logging there.

For SSH, configure and verify normal key/agent authentication and the server fingerprint outside Cogitide first. The desktop accepts a hostname or an OpenSSH configuration alias (which can specify user, port and jump host); no password prompting or automatic host-key acceptance is performed. The remote account needs a POSIX-compatible shell. For WSL, install the distribution separately and use that distribution's Linux package/path. Native Windows remains supported as a local Host; SSH remote startup expects POSIX command semantics.

Provider keys, account credentials, Git, language toolchains, MCP/LSP configuration and browser dependencies belong to the endpoint. Connections accepts an API key for that endpoint's native credential vault, or an existing environment variable in the noninteractive Host environment. An unavailable vault fails explicitly. The desktop does not copy local model keys or account stores. Playwright browser checks additionally require the matching browser/system dependencies; ordinary file/command operations do not require a running GUI.

In foreground mode, closure of the owning desktop connection sends EOF and stops Host and its owned commands. In explicitly selected durable mode, bridge EOF or network loss detaches the client while the remote service continues accepted work. Reconnect to the pinned service identity to restore bounded journal/message pages and pending approvals. It never automatically replays an action. Explicit service stop ends its owned commands; a service crash or endpoint reboot requires recovery and explicit continuation of stopped tasks. This is a per-user service rather than a system boot-time service installation.

To upgrade, stop the connection, keep the existing private data directory, deploy a new complete package in a different directory, then update the profile's executable path. Keep the old package until the new connection is verified. Do not merge partial versions or replace libraries while Host is running.

LICENSE, NOTICE, THIRD_PARTY_NOTICES.md and component Licenses must accompany redistribution. Current source/setup: [Cogitide](https://github.com/Yang916-yy/cogitide), [remote connections](https://github.com/Yang916-yy/cogitide/blob/main/src/Workbench.Desktop/Remote/README.md).
