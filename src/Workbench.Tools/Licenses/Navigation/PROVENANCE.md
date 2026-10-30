# Navigation tools: sources and deliberate differences

Implemented 2026-10-30. `NavigationTools.cs` extends the existing workspace service with line/character-paged reads, literal/regular-expression search and file globs. Tool registration is kept in the existing `WorkspaceTools` catalog. No shell service, parallel index or external navigation daemon was introduced.

## OpenCode behavior adapted

Source: [anomalyco/opencode](https://github.com/anomalyco/opencode), fixed revision `60072747957926e7aeec7e911fe6e23abe9bf830`, MIT. The exact repository notice is retained in `OpenCode-MIT.txt`.

| Source module | Behavior retained in the C# adaptation |
| --- | --- |
| [read.ts](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/tool/plugin/read.ts) | Explicit page offset/limit, line labels separate from content, a bounded response with an actionable continuation position. |
| [grep.ts](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/tool/plugin/grep.ts) | Literal or regular-expression matching, case selection, file-pattern filtering, line-oriented matches and clear truncation/narrowing instructions. |
| [glob.ts](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/tool/plugin/glob.ts) | A dedicated path-pattern query with scope, a result limit and explicit partial-results metadata. |

This is a focused adaptation of navigation semantics, not a full port of OpenCode's Effect services, ripgrep integration, media/PDF ingest, external-directory authorization or instruction-discovery framework. Cogito preserves its workspace-relative path checks, rejects symlinks/junctions and Git internals, and lets Git own ignore rules. Glob filters never re-enumerate the filesystem independently of this checked inventory.

Differences are explicit: offsets remain **zero-based** to match Cogito's existing APIs, displayed line/column labels are one-based, and text search remains **literal and case-insensitive by default** for old callers. Regex uses .NET syntax, not ripgrep syntax. Globs use Microsoft's `*`/`**` matcher; brace expansion and every ripgrep extension are not promised. Path matching follows platform casing rules; result order is ordinal path order, then ascending line number.

## Mature .NET implementation

**Microsoft.Extensions.FileSystemGlobbing 10.0.14**, Microsoft, MIT. The NuGet package was inspected directly: repository `https://github.com/dotnet/dotnet`, revision `0427b9abbead3fa64c6655eb4290c537397131fa`, license expression MIT. The exact revision's `LICENSE.TXT` is retained as `DotNet-MIT.txt`; the package's unmodified `THIRD-PARTY-NOTICES.TXT` is retained as `Globbing-THIRD-PARTY-NOTICES.txt`. [Package metadata](https://www.nuget.org/packages/Microsoft.Extensions.FileSystemGlobbing/10.0.14), [official in-memory Match documentation](https://learn.microsoft.com/en-us/dotnet/core/extensions/file-globbing#match-overloads).

`Matcher.Match` evaluates checked paths in memory. `FileStream`, strict `UTF8Encoding`, `StreamReader`, SHA-256/`IncrementalHash`, `Regex` with a 100 ms per-line timeout, System.Text.Json and cancellation are BCL facilities. The code adds cursor accounting and existing service integration; it does not implement another regex or glob parser.

## Bounds, snapshots and continuation

- A readable navigation file is at most **16 MiB** of original bytes. Reads hash every original byte, including a UTF-8 BOM, then decode strictly. Binary/UTF-16/invalid UTF-8 inputs fail. Size/mtime checks detect ordinary concurrent edits; this is not an OS-wide transactional snapshot against arbitrary external writers.
- A read page contains at most 12,000 text characters and at most 80,000 serialized JSON characters. `path`, `sha256` and `content` remain available to existing evidence/hash consumers. `nextOffset` and `nextCharacterOffset` can resume in the middle of an oversized line without splitting Unicode scalars or CRLF. Pass `expectedSha256` on subsequent pages to reject changed file contents. Read expansion does not silently expand the separate write/edit size limit.
- Search considers at most 2,000 files / 32 MiB of scanned file bytes from the bounded inventory. Partial scans report `scanTruncated`, skipped files and narrowing guidance. Matching-line offsets only paginate that bounded scan; they do not pretend to page beyond its scan budget. A late match on a long line is shown using a preview centered near the match, with its true column and preview offset.
- Search `snapshot` hashes query options, inventory, scanned readable file content hashes and scan boundaries; `expectedSnapshot` rejects changed pages. Skipped file contents and files outside the scan are not claimed to be verified. Glob snapshots track matching names only, explicitly not contents. Neither snapshot is a persistent database or an atomic multi-file transaction.
- Existing inventory limits remain 20,000 files / 2,000,000 Git output characters. `pathPrefix` now narrows Git's pathspec or the fallback starting directory before those budgets, so a small subtree is not lost solely because another subtree filled the root inventory. Response shrinking occurs at record boundaries.

## Verification ownership

`NavigationUpgradeTests` covers full-byte hashes, BOM/Unicode/newline fidelity, oversized-line cursors, larger files and late line offsets, stale read/search/glob pages, literal compatibility, regex/case/context filters, timeouts, ignored-file boundaries, Unicode response budgets and scan-budget narrowing. `RepositoryNavigationTests` continues to cover Git ignore behavior, old registration arguments, symbols and existing page contracts.

Tests are deterministic local fixtures. Final build and test results belong in the integration report; no pass count is asserted by this provenance file.
