**English** | [简体中文](release.zh-CN.md)

# Development preview packages

This English summary preserves the **2026-10-30 research preview release record**. Public branding is Cogitide; executables remain Workbench.Desktop/Workbench.Host and user data remains Workbench. The historical `research-preview/{win-x64,linux-x64,osx-arm64}` package included the shared kernel, seventeen base tools and optional history/compaction/prefetch. Offline optimization was explicit and never activated exports. Real ChatGPT access and Linux/macOS device acceptance were still pending. Later integration records supersede capability/count details, not this release's historical evidence.

Keep the entire package directory, DLLs/native libraries/subdirectories together. `publish-info.json` identifies architecture/runtime/self contained status. Default packages need no SDK; Git and project language toolchains remain external requirements, without Codex CLI.

Launch Windows with `start.cmd` or Workbench.Desktop.exe and Linux/macOS with `sh ./start.sh` on the matching architecture. Scripts restore lost executable bits where needed and set WORKBENCH_HOST_PATH to the colocated Host. User state is in local application data/Workbench; updates use a new complete directory after closing the app. Old Codex history stays readable but continuation creates a new Cogitide chat; old login files are not read/migrated. `--data-dir` and `--workspace` permit explicit isolated state/project locations.

API keys come from configured environment variables (default DEEPSEEK_API_KEY), never package/project files. Restart or launch from the configured shell after setting them. Official account login is requested in connection settings and opens the official authorization page. Independent accounts can be added/selected/logged out; catalog/access follow actual granted entitlement. Public Responses inference runs in Cogitide's kernel rather than an external agent. Windows DPAPI/macOS Keychain/Linux Secret Service protect credentials; failure never falls back to plaintext. API paths also support Responses, Chat Completions, DeepSeek and Claude.

Read only/workspace edit/full access are application tool/approval policies, not an OS sandbox. Background commands stop on run end/cancel/Host loss; at most four MiB logs can be reread without automatic command replay. Multi file patches precheck paths/hashes and attempt controlled rollback but are not a crash atomic transaction.

Bundled .NET does not supply OS graphics dependencies. Linux needs its graphic session and Avalonia/.NET native libraries. No Windows signing, macOS Developer ID/notarization or .app installer was created in this snapshot. Package hashes, Host smoke tests and real GUI acceptance are separate; publish-info GUI fields never become passed just because packaging succeeded. IME/scaling/pickers/terminal/window behavior require actual devices.

Verify SHA256SUMS with `sha256sum -c` (Linux) or `shasum -a 256 -c` (macOS). Hashes detect modification but are not publisher signatures. Source scripts `verify-release.py`, `verify-tools-upgrade.py`, `build.ps1` and `publish.ps1` check architecture/runtime/locks/notices and generated local Host fixtures. `--smoke-host` runs only a matching OS/architecture in isolated state without model/login; `--require-source-locks`, `--require-unified-kernel` and `--require-tool-upgrade` select corresponding evidence checks. Historical packages can omit later requirements. Publishing rejects nonempty destinations; FrameworkDependent packages require a separately installed matching runtime.

Retain LICENSE, NOTICE, THIRD_PARTY_NOTICES.md and module Licenses. Original project code is Apache 2.0; third party portions retain their own terms. Exact commands/paths from this release remain in the full Chinese record rather than being presented as current release output.

This English page summarizes the preserved source. [The full Chinese record](release.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)
- [Original reference 2](https://docs.avaloniaui.net/docs/deployment/linux)
- [Original reference 3](https://support.apple.com/en-us/102445)
