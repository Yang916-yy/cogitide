**English** | [简体中文](dotnet-reuse-audit.zh-CN.md)

# .NET and component reuse audit

Historical review dated **2026-10-30**. The conclusion was BCL + official SDKs + a small set of mature components, with custom work concentrated on task state, approvals, context and recoverable ownership. Framework updates were not measured performance gains. Later integration can supersede then deferred features without rewriting this audit's results.

Reused components included SocketsHttpHandler/HttpClient for HTTP/TLS/pools; official OpenAI .NET **2.14.0** and System.ClientModel **1.15.0** for requests/SSE; generated System.Text.Json stable DTO metadata with dynamic fallback; FrozenDictionary tools; Git CLI inventory/ignore/status/diff; Microsoft.Extensions.FileSystemGlobbing **10.0.14** over checked paths; BCL Regex with **100 ms/line** limit; Roslyn **5.9.0** syntax trees; YamlDotNet **18.1.0** scalar frontmatter; Microsoft.Data.Sqlite **10.0.14** transactions/events; ResourceManager/.resx/CultureInfo; Avalonia **12.1.4** controls/animations; AvaloniaEdit/Markdig/native terminal/Porta.Pty/XTerm.NET. Avalonia is a third party UI framework, not a BCL native widget guarantee.

Application code owns budgets, checked paths, schema compatibility, complete stream/tool aggregation, immutable evidence, hash conflicts, whole record pagination, event ordering and unknown result recovery. Shared HTTP policy used five minute connection lifetime, two minute idle, thirty second connect timeout and five minute SDK network timeout; cookies/redirects/retries were disabled. MaxConnectionsPerServer sixteen was not a global HTTP/2 stream budget. Official default transports already pooled connections: no prior pool leak was established. Saved model errors excluded raw service headers/bodies/secrets, without claiming all arbitrary tool content was sanitized. Evidence still loaded underlying event collections without paginated I/O.

C# 15 closed RunOutcome hierarchies/exhaustive switches were adopted. Async streams/cancellation/WaitAsync/ArgumentList/Regex/hashing/source generation/FrozenDictionary are mature capabilities, not all .NET 11 inventions. Metadata generation retained compatibility/reflection fallback without fast path/AOT speed claims. No product JIT/GC/startup/memory/throughput benchmarks existed.

Deferred APIs were full capture Process.RunAndCaptureTextAsync (would lose streaming/budget/tree constraints), ReadAllLinesAsync (wrong for NUL Git and unbounded long output), KillOnParentExit (needed real OS/Host death tests), JSONL async enumerable serialization (needed per item flush/response interleaving semantics), custom connection eviction/initial HTTP2 stream tuning (no measured need), and full AOT/trimming (reflection/UI/native compatibility). Official KillOnParentExit documentation differed by revision; neither established macOS support. Closed historical runtime issues did not prove pinned RC3 broken. Owned worker stdin EOF cancellation was implemented instead, without claiming arbitrary detached descendants were contained.

Fixed adaptations: OpenCode read/grep/glob **60072747957926e7aeec7e911fe6e23abe9bf830**; focused patch port **810c0eba1a7348036f8bdf61e6632d0c36c6876c**; Codex unified exec behavior **788b9d7bf6e37f735647b7f84b556c2a807df4d8**. Retained original MIT/Apache/NOTICE records distinguish code ports from references. Exact unique matching, hashes, preflight/backup/controlled rollback and run/pipe/log ownership were project additions, without entire upstream runtimes.

MAF/MCP were evaluation candidates at this snapshot. Any later framework must preserve existing contracts/fault tests rather than replace product semantics with a single SDK call. Syntax symbols explicitly had semanticAnalysis false and were not type/call graph/build evidence. A non Git fallback did not parse gitignore; broken Git repositories never silently scanned ignored contents. **Do not reinvent infrastructure:** new custom components require a concrete missing capability, mature alternatives and measured validation.

This English page summarizes the preserved source. [The full Chinese record](dotnet-reuse-audit.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](../src/Workbench.Core/Models/ModelHttpTransport.cs)
- [Original reference 2](../src/Workbench.Core/Models/OpenAiCompatibleModel.cs)
- [Original reference 3](../src/Workbench.Protocol/WireJsonContext.cs)
- [Original reference 4](../src/Workbench.Protocol/Contracts.cs)
- [Original reference 5](../src/Workbench.Core/Runtime/AgentEngine.cs)
- [Original reference 6](../src/Workbench.Tools/Navigation/RepositoryInventory.cs)
- [Original reference 7](../src/Workbench.Tools/Navigation/NavigationTools.cs)
- [Original reference 8](../src/Workbench.Tools/Navigation/CSharpSymbols.cs)
- [Original reference 9](../src/Workbench.Tools/Editing/PatchTools.cs)
- [Original reference 10](../src/Workbench.Tools/Execution/CommandSessions.cs)
- [Original reference 11](../src/Workbench.Core/Projects/ProjectRules.cs)
- [Original reference 12](../src/Workbench.Core/PROJECT-REUSE.md)
- [Original reference 13](../src/Workbench.Core/Persistence/SqliteSessionStore.cs)
- [Original reference 14](../src/Workbench.Desktop/Localization.cs)
- [Original reference 15](../src/Workbench.Desktop/UiMotion.cs)
- [Original reference 16](../src/Workbench.Desktop/DEPENDENCIES.md)
- [Original reference 17](../src/Workbench.Tools/DEPENDENCIES.md)
- [Original reference 18](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines)
- [Original reference 19](https://raw.githubusercontent.com/Azure/azure-sdk-for-net/main/sdk/core/System.ClientModel/src/Pipeline/HttpClientPipelineTransport.cs)
- [Original reference 20](https://github.com/openai/openai-dotnet)
- [Original reference 21](../src/Workbench.Core/ANTHROPIC.md)
- [Original reference 22](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15#closed-hierarchies)
- [Original reference 23](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)
- [Original reference 24](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/libraries)
- [Original reference 25](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.killonparentexit?view=net-11.0)
- [Original reference 26](https://github.com/dotnet/runtime/issues/134990)
- [Original reference 27](../src/Workbench.Tools/Licenses/Navigation/PROVENANCE.md)
- [Original reference 28](../src/Workbench.Tools/Licenses/Patch/PROVENANCE.md)
- [Original reference 29](../src/Workbench.Tools/Licenses/Commands/PROVENANCE.md)
- [Original reference 30](https://learn.microsoft.com/en-us/dotnet/core/extensions/file-globbing#match-overloads)
- [Original reference 31](../tests/Workbench.Tests/ProviderTransportTests.cs)
- [Original reference 32](../tests/Workbench.Tests/RepositoryNavigationTests.cs)
- [Original reference 33](../tests/Workbench.Tests/NavigationUpgradeTests.cs)
- [Original reference 34](../tests/Workbench.Tests/PatchTests.cs)
- [Original reference 35](../tests/Workbench.Tests/CommandSessionTests.cs)
- [Original reference 36](../tests/Workbench.Tests/EvidenceToolUpgradeTests.cs)
- [Original reference 37](../tests/Workbench.Tests/ProtocolSerializationTests.cs)
- [Original reference 38](../tests/Workbench.Tests/DesktopBindingTests.cs)
- [Original reference 39](../docs/implementation-status.md)
