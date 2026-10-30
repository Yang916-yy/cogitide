# File navigation and C# source semantics

| File | Responsibility |
| --- | --- |
| [RepositoryInventory.cs](RepositoryInventory.cs) | Bounded file inventory using Git's standard ignore rules, with an explicitly identified fallback outside Git worktrees. |
| [NavigationTools.cs](NavigationTools.cs) | Paged UTF-8 reads, long-line continuation, content snapshots, literal/regex/glob search and surrounding context. |
| [CSharpSymbols.cs](CSharpSymbols.cs) | Roslyn syntax declarations, signatures, source lines and syntax diagnostics. This outline is syntax-only. |
| [SemanticNavigation.cs](SemanticNavigation.cs) | Roslyn source-compilation definitions, exact-symbol references and compiler diagnostics, with explicit project boundaries and resource budgets. |

## Reused components and provenance

Read/search/glob product behavior is adapted from **OpenCode commit `60072747957926e7aeec7e911fe6e23abe9bf830`**. Fixed source files, deliberate differences and the retained MIT license are recorded in [Navigation provenance](../Licenses/Navigation/PROVENANCE.md). This is a focused C# adaptation of needed behavior; it does not copy OpenCode's TypeScript filesystem or event framework.

Globs reuse **Microsoft.Extensions.FileSystemGlobbing 10.0.14**. Regular expressions reuse BCL `Regex`, with a timeout per line. Both C# tools reuse **Microsoft.CodeAnalysis.CSharp 5.9.0** directly; semantic navigation uses `CSharpCompilation`, `SemanticModel`, `GetDeclaredSymbol`, `GetSymbolInfo` and `SymbolEqualityComparer`, rather than a homemade parser or a text-based symbol index. It is project-specific integration of Roslyn's existing APIs, not a port of another language server. Git owns ignore semantics; path checks, UTF-8 reading, JSON, hashes and cancellation reuse the existing workspace service and .NET facilities. Exact dependencies and other navigation budgets are listed in [DEPENDENCIES](../DEPENDENCIES.md).

Searching and reading on demand can support DCI-style direct corpus interaction, but these are primarily mature engineering tools. Their presence is not a complete DCI paper reproduction or evidence of measured research benefit. Frozen-history retrieval is documented separately in [RepositoryMemory](../RepositoryMemory/README.md).

## Allocation-sensitive implementation

Paged reads reuse BCL `ArrayPool<byte>` with cleared returns, bounded `FileStream.ReadAtLeastAsync`, strict UTF-8 span decoding and full raw-byte SHA-256. Search retains context line positions until an actual returned match needs strings; it still scans all eligible bytes for the same snapshot, total counts and regex timeout behavior. Git NUL parsing uses BCL span splitting. These are .NET component adaptations, not a new parser/index/cache. Regression cases cover pooled tails, BOM/multibyte boundaries, malformed UTF-8 after a requested page, same-length edits, context order and later regex timeouts. See [measured results and rejected experiments](../../../docs/performance-tuning.md); reduced allocation does not by itself prove a latency or resident-memory improvement.

## `csharp_semantic`

The tool accepts `action` (`definition`, `references`, or `diagnostics`) and a workspace-relative `.cs` `path`. `line` and `column` default to 1 and use **one-based UTF-16 code units**, matching Roslyn/.NET strings. Source result end positions are exclusive. Diagnostics without a source location use an empty path and zero positions.

```json
{"action":"definition","path":"src/Example/Consumer.cs","line":12,"column":19}
```

`definition` resolves the symbol at the requested token and returns its source locations. Metadata-only BCL symbols return an empty source result list with `symbol.metadataOnly=true`. `references` compares bound Roslyn symbols, so overloads, unrelated same-name types and shadowed locals remain distinct; comments and string contents do not become references. Constructed generic methods, reduced extension methods and partial method implementations normalize to their original declaration. Results cover explicit bound names/types and constructor/operator uses. Declarations are excluded; override families, reflection, dynamically resolved calls and string-based references are not expanded into an inferred call graph. An unresolved or ambiguous symbol returns an error instead of choosing an arbitrary candidate.

`diagnostics` returns warning/error diagnostics from the bounded source compilation, including binding/type errors that a syntax outline cannot find. These results are not a successful build or test claim.

Reference scanning uses Roslyn's decoded identifier `ValueText` to skip different-name candidates for ordinary/local methods, ordinary fields/properties, parameters and locals before binding. Matching names still require exact Roslyn symbol equality. Types and aliases, constructors, operators, conversions, tuple element aliases and explicit interface properties retain full semantic binding. A compiler-confirmed `nameof` method group with one member uses Roslyn's `GetMemberGroup`; overloaded groups remain unresolved. The selected file's semantic model is reused only inside that request. Every syntax node still participates in the existing budget/cancellation checks; no source or compilation cache is introduced. Regression fixtures cover `nameof`, escaped/Unicode identifiers, overloads/generics, constructor overloads and type aliases. Latency and allocation tradeoffs are measured separately in the performance report.

### Source scope and execution boundary

The nearest ancestor directory containing a `.csproj`, within the selected workspace, is the default boundary. Project files are **filename markers only**. Files belonging to a deeper project boundary are excluded; neighboring projects are not merged into the compilation. Without a containing project marker, the workspace is the boundary and nested project directories remain excluded. Multiple `.csproj` files in one directory are reported as markers for that shared directory; the tool does not pretend to choose between their build configurations.

Sources come from the existing `RepositoryInventory`: Git evaluates standard ignores (including tracked files), and the non-Git fallback reports that it cannot evaluate gitignore. An explicitly requested ignored source is rejected. Existing workspace traversal, Git-internal and symlink/junction checks apply.

The tool never evaluates MSBuild, runs repository scripts, restores packages, emits an assembly, or invokes source generators/analyzers. It references only BCL assembly metadata advertised by the current process's trusted platform assembly list and located alongside the runtime's core library. It does not load repository DLLs, NuGet dependencies or `ProjectReference` outputs. Project `Compile` includes/removes, conditional constants, target frameworks, implicit SDK global usings and other evaluated project settings are not applied. Missing dependencies or build configuration can therefore produce diagnostics different from the real project. Inspect `scope.completeProject=false` and the returned scope before acting on the result. This is bounded source semantics, not a full project workspace or LSP.

### Bounds and pagination

Each request permits at most **256 source files**, **1 MiB per source**, **8 MiB total source bytes**, **250,000 syntax nodes/tokens**, syntax depth **128**, **10,000 result records**, and **80,000 serialized JSON characters**. Boundary probing is limited to 4,096 directories and 32 project markers in one directory. A 20-second cooperative cancellation budget complements the caller's cancellation token. Input/analysis limits fail explicitly without returning a partial compilation as a complete semantic answer. Output pages shrink at whole-record boundaries and expose `responseTruncated` and `nextOffset`.

Pass `expectedSnapshot` from the first response when continuing with `nextOffset`; retain the same action/path/position. The snapshot includes the query, scope, selected source inventory and every compiled source's content hash, plus compiler/runtime reference identity information. Same-length source edits and added/deleted selected sources invalidate it. Each request rereads the bounded sources; there is no global compilation or user-content cache. The snapshot is not an OS-atomic transaction across files or arbitrary concurrent external writers.

## Verification

[SemanticNavigationTests](../../../tests/Workbench.Tests/SemanticNavigationTests.cs) exercises actual Roslyn binding across files, overload/name/local isolation, generic normalization, ambiguity, compiler diagnostics, project boundaries without execution, Git ignores, same-length edits and inventory changes between pages, UTF-16 positions, metadata symbols, input/depth budgets, bounded Unicode pagination, tool path checks and cancellation. Existing [NavigationUpgradeTests](../../../tests/Workbench.Tests/NavigationUpgradeTests.cs) and [RepositoryNavigationTests](../../../tests/Workbench.Tests/RepositoryNavigationTests.cs) cover the read/search/glob/outline contracts.

These are deterministic local functional fixtures. Build/test pass counts and measured timings belong to the current integration report; this README does not claim a measured navigation speedup or research result.

**Do not rebuild mature infrastructure unnecessarily.** Reuse Git, Microsoft's globbing/Roslyn libraries, BCL regex and the common path boundary. A new parser, matcher or index needs a concrete unmet capability, fixed provenance/license, and comparable-budget validation evidence.

## 中文入口

本模块提供有界文件清单、分页精读、搜索/glob、C# 语法轮廓和 Roslyn 源码语义导航。`csharp_semantic` 可查定义、精确符号引用和编译诊断；默认以最近的 `.csproj` 所在目录划界，排除嵌套和相邻项目。项目文件只作为边界标记，不执行 MSBuild、脚本、生成器或分析器，也不加载 NuGet/ProjectReference。它不是完整 LSP 或真实项目构建。行列按一基 UTF-16 计数，续页应携带 `expectedSnapshot`；修改已编译源码会使旧快照失效。完整使用范围、资源预算、依赖来源及验证说明见上文。
