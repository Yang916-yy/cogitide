# Explicit multi-language LSP integration

`LspToolSet` supplies the approved `lsp_query` tool: `definition`, `references`, `hover`, file `diagnostics`, and document `symbols`. Installed TypeScript/JavaScript, Python, C#, Go and Rust servers use the same stdio adapter. A server must advertise the requested capability. This is a focused coding-agent integration, not full IDE parity or a language-server implementation.

`StreamJsonRpc` **2.26.150** owns Content-Length framing, JSON-RPC parsing, concurrent server callbacks, request IDs, and protocol cancellation. It is reused directly, not ported. System.Text.Json owns configuration and response projection; `Process` owns launching, the existing `WorkspaceService.StopProcessAsync` owns process-tree cleanup (including its macOS runtime workaround), and `WorkspaceService.InventoryAsync` owns Git ignores and checked inventory. No second watcher, parser, filesystem matcher, process scheduler, storage layer, or auto-installer is introduced.

## Configuration, trust and approval

The host selects a local user configuration path and calls `LoadAsync(path, workspace, excludedEnvNames, runCancellationToken)`. An absent file disables LSP. Repository content is never searched for executable configuration. Loading only validates configuration; the first approved tool query starts a server. The tool always has `RequiresApproval=true`, so the existing engine excludes it in ReadOnly mode and applies ordinary application permissions, project rules and approval checks. These permissions are not an OS sandbox.

Each server requires **`trustedWorkspace: true`**. Servers may read files, load plugins, analyzers or project configuration, invoke builds, and otherwise run with the user's privileges; enable them only for a trusted root. C# servers can evaluate MSBuild; Rust and Go servers can invoke build tooling. This trust grant does not bypass the tool's launch approval. Server-requested `workspace/applyEdit` is rejected, and the client exposes no execute-command tool. No command is installed, inferred from repository content, or run via a shell shim. Commands are existing absolute executable paths, with arguments passed separately. On Windows, configure `node.exe` plus a `.js`/`.mjs` entrypoint rather than an npm `.cmd` wrapper.

```json
{
  "servers": [
    {
      "id": "typescript",
      "command": "C:\\Program Files\\nodejs\\node.exe",
      "arguments": ["C:\\user-managed-tools\\typescript-language-server\\lib\\cli.mjs", "--stdio"],
      "languages": ["typescript", "typescriptreact", "javascript", "javascriptreact"],
      "root": ".",
      "trustedWorkspace": true,
      "initializationOptions": {
        "disableAutomaticTypingAcquisition": true,
        "tsserver": { "useSyntaxServer": "never" }
      },
      "requestTimeoutSeconds": 30
    }
  ]
}
```

Server IDs are unique; at most eight servers are allowed. Supported language IDs are `typescript`, `typescriptreact`, `javascript`, `javascriptreact`, `python`, `csharp`, `go`, `rust`. Roots are existing workspace-relative directories. Ambiguous matches require `serverId`. `settings` supports `workspace/configuration` section queries; `initializationOptions` is passed only during initialization. Only a small OS/runtime environment allowlist is inherited. Additional `environmentVariables` are names of explicitly allowed parent environment values; provider credentials and the host's excluded account variables remain excluded. Do not put secret values in configuration.

Typical user-managed executable/argument combinations (install separately, verify the selected server's documentation):

| Languages | Executable and arguments |
| --- | --- |
| TypeScript / JavaScript | Absolute `node` + absolute `typescript-language-server/lib/cli.mjs`, `--stdio`; its matching `typescript` package must already be available. |
| Python | Absolute `node` + absolute `pyright/dist/pyright-langserver.js`, `--stdio`. |
| C# | Absolute `csharp-ls`, or absolute `dotnet` + a separately installed server DLL, using that server's stdio arguments. |
| Go | Absolute `gopls`, `serve`. |
| Rust | Absolute `rust-analyzer`. |

Only TypeScript and Python have opt-in real-server acceptance fixtures here; the other language configurations share the tested adapter but are not a claim of real-server acceptance.

The TypeScript example explicitly uses the documented single semantic-server mode. In typescript-language-server 5.1.3, the default `useSyntaxServer: "auto"` serves syntax IntelliSense while projects load; an immediate first definition can therefore return a local import alias before semantic project binding is ready. `"never"` makes the first query use the semantic server. This is user-selected server configuration, not an inferred alias rewrite, hidden repeated query or fixed startup sleep. See the [upstream v5.1.3 configuration contract](https://github.com/typescript-language-server/typescript-language-server/blob/v5.1.3/docs/configuration.md#tsserver-options). Other server readiness and project-evaluation behavior remains owned by the selected language server.

## Positions, evidence and limits

Tool inputs and returned ranges are **one-based UTF-16**; result ends are exclusive. Initialization advertises UTF-16, UTF-8 and UTF-32, and conversion uses BCL Unicode scalars. Split surrogate/scalar positions and out-of-line positions fail. Definitions accept both Location and LocationLink. References exclude declarations. Hierarchical document symbols are flattened. Hover and diagnostic text are untrusted server content, bounded before return. Only local file locations in the checked source snapshot are returned; external/dependency/ignored/invalid locations are counted as omitted rather than exposing their file content.

File URIs are canonicalized through BCL `Uri`/`Path` before the existing workspace checks. The narrow Windows drive-prefix normalization accepts VS Code's `file:///c%3A/...` representation as the same checked file as `.NET`'s `file:///C:/...`; diagnostics use canonical keys with Windows path casing. It does not authorize nonlocal authorities, arbitrary relative paths or locations outside the bounded workspace snapshot.

Each request uses the existing Git-aware inventory under the configured root, with at most **512 source/configuration files**, **1 MiB per file**, and **16 MiB total bytes**. Relevant source files plus JSON/TOML/YAML/project/build-marker files form the snapshot. The client sends didOpen/full didChange/didClose and explicit changed-file notifications on demand, without a filesystem watcher. It opens all bounded matching source files so cross-file changes are synchronized. No unsaved editor buffer is inferred.

Source/configuration hashes are checked before and after analysis. A continuation `expectedSnapshot` covers the query, configuration, source/configuration bytes, and projected result records; source edits, inventory changes, or changed asynchronous server results reject continuation. This is not a transactional filesystem snapshot, nor a snapshot of installed server dependencies, generated/ignored files, external files, or the server's entire project state. `scope.completeProject=false` makes this boundary explicit. Select a smaller root when an input budget is exceeded; input truncation never silently becomes a complete result.

Result projection permits 10,000 records, a 1–500 record page, and 60,000 serialized JSON characters. Large text is clipped, dropped locations are counted, and `truncated`/`nextOffset` expose continuation. Push diagnostics are returned only after a matching document publication; mismatched versions are discarded. Unversioned publications have `diagnosticVersionVerified=false`. If no publication arrives within five seconds, `diagnosticsObserved=false` distinguishes unavailable results from an observed empty list. Pull diagnostic providers use `textDocument/diagnostic`. A publication is the server's observed state, not proof that every asynchronous analysis phase has completed.

Servers are run-owned. Caller cancellation, timeout, host/run cancellation or disposal stops owned processes and closes RPC; incomplete requests are never replayed. Stderr is continuously drained without logging workspace paths or secret-bearing diagnostics. Cleanup failure remains an error. The host must dispose the tool set on normal completion and supply its run/host cancellation token for loss handling.

## Primary sources and adaptation boundary

OpenCode's [LSP client](https://github.com/anomalyco/opencode/blob/33298e87758685d6bea5b230a0133902ca66807c/packages/opencode/src/lsp/client.ts) and [LSP query adapter](https://github.com/anomalyco/opencode/blob/33298e87758685d6bea5b230a0133902ca66807c/packages/opencode/src/lsp/index.ts), fixed v1.1.35 revision **`33298e87758685d6bea5b230a0133902ca66807c`**, are behavior references for initialization, workspace settings, document synchronization, diagnostic observation and standard query methods. Its TypeScript/Bun runtime, event bus, automatic server installation, permissive dynamic registrations and filesystem abstractions are not ported. No source code is copied. The retained [OpenCode MIT license](Licenses/OpenCode-MIT.txt) identifies its original notice.

Microsoft's [StreamJsonRpc](https://github.com/microsoft/vs-streamjsonrpc/tree/aac34050e547328910f33faeeec9eab909f3785e), fixed package revision **`aac34050e547328910f33faeeec9eab909f3785e`**, is reused via its published NuGet package and [MIT license](Licenses/StreamJsonRpc-MIT.txt). [Official connection documentation](https://microsoft.github.io/vs-streamjsonrpc/docs/connecting.html) describes paired stdio streams and header-delimited messages. Microsoft's [LSP 3.17 specification](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/) defines synchronization, capabilities, locations, negotiated position encodings and diagnostics. Cogitide adds bounded source evidence, path filtering, approval integration, credential exclusions and owned cleanup; these are engineering mechanisms, not measured research benefits.

## Verification

[LspIntegrationTests](../../../tests/Workbench.Tests/LspIntegrationTests.cs) uses an actual local Python stdio protocol peer to exercise lifecycle, approval registration, checked paths, negotiated Unicode positions, all five actions, push/pull and stale-version diagnostics, snapshot invalidation, environment exclusions and cancellation. Its framing code is confined to the test peer; production framing belongs to StreamJsonRpc.

[LspRealServerTests](../../../tests/Workbench.Tests/LspRealServerTests.cs) is opt-in and never installs tools. Set `COGITIDE_TEST_TS_LSP_COMMAND` (absolute Node executable) and `COGITIDE_TEST_TS_LSP_SCRIPT` (absolute TypeScript language-server entrypoint), and/or `COGITIDE_TEST_PY_LSP_COMMAND` and `COGITIDE_TEST_PY_LSP_SCRIPT` (Pyright entrypoint). The tests check cross-file definitions/references, hover, symbols and a real type diagnostic. Missing executable/script paths produce explicit skipped tests. Current package versions and pass counts belong in the integration report, not as unverified claims here.
