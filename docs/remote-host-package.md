# Cogitide remote Host package

This is the headless Host, without the desktop UI. Keep the **complete directory** together. Use a package matching the endpoint operating system and architecture. `publish-info.json` records the pinned runtime and package kind; `SHA256SUMS` detects changed files and is not a publisher signature.

On Linux/macOS, allow the native Host to execute:

```sh
chmod u+x Workbench.Host
```

The desktop launches this executable through system OpenSSH or WSL with `--stdio`. Set its **absolute endpoint path** in a saved Host connection. An optional data directory must also be an absolute endpoint path. Keep that state separate from source, the package directory, and any other concurrently running Host. Stdio stdout is the JSON protocol; do not add banners or logging there.

For SSH, configure and verify normal key/agent authentication and the server fingerprint outside Cogitide first. The desktop accepts a hostname or an OpenSSH configuration alias (which can specify user, port and jump host); no password prompting or automatic host-key acceptance is performed. The remote account needs a POSIX-compatible shell. For WSL, install the distribution separately and use that distribution's Linux package/path. Native Windows remains supported as a local Host; SSH remote startup expects POSIX command semantics.

Provider keys, account credentials, Git, language toolchains, MCP configuration and browser dependencies belong to the endpoint. Configure the relevant environment variables in the noninteractive environment which actually launches Host. The desktop does not copy local model keys or account stores. Playwright browser checks additionally require the matching browser/system dependencies; ordinary file/command operations do not require a running GUI.

Normal closure of the owning desktop connection sends EOF and stops this Host and its owned commands. A network partition can delay EOF or stop confirmation; inspect the endpoint after reconnect. Histories and checkpoints stay in its data directory. Reconnecting reads that state; continue stopped tasks explicitly rather than replaying commands. This package does not install a daemon or promise execution while the desktop is disconnected.

To upgrade, stop the connection, keep the existing private data directory, deploy a new complete package in a different directory, then update the profile's executable path. Keep the old package until the new connection is verified. Do not merge partial versions or replace libraries while Host is running.

LICENSE, NOTICE, THIRD_PARTY_NOTICES.md and component Licenses must accompany redistribution. Current source/setup: [Cogitide](https://github.com/Yang916-yy/cogitide), [remote connections](https://github.com/Yang916-yy/cogitide/blob/main/src/Workbench.Desktop/Remote/README.md).
