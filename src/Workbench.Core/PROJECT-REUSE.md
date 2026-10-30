# Project rules, skills and procedure memory

Implemented 2026-10-30. These are focused C# ports of mature discovery behavior plus a small evidence-linked procedure memory. There is no external memory service, vector database, remote skill installation or script runner.

## Ported sources

| Source | Fixed revision and paths | C# adaptation |
| --- | --- | --- |
| Pi | `e7a0df27c09d11bf4161d3605246cbf57a129cb2`, [skills.ts](https://github.com/earendil-works/pi/blob/e7a0df27c09d11bf4161d3605246cbf57a129cb2/packages/coding-agent/src/core/skills.ts), `loadSkillsFromDirInternal`, `loadSkillFromFile`, `formatSkillsForPrompt` | `ProjectRules.DiscoverSkillsAsync`, `ReadSkillAsync`, `FormatSkills`: a `SKILL.md` stops recursion, direct root Markdown is accepted, other directories are traversed, metadata/catalog is loaded before the skill body, duplicate names keep the earlier source, model-disabled skills require an explicit user selection. |
| OpenCode | `60072747957926e7aeec7e911fe6e23abe9bf830`, [config/plugin/instruction.ts](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/config/plugin/instruction.ts), [instruction-discovery.ts](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/instruction-discovery.ts), [session/instructions.ts](https://github.com/anomalyco/opencode/blob/60072747957926e7aeec7e911fe6e23abe9bf830/packages/core/src/session/instructions.ts) | `ProjectRules.LoadAsync`: ancestor AGENTS.md discovery, file-version changes/removals, and re-injection when model-visible context is rebuilt. Cogito presents root-to-target scope order, reloads bounded files at request boundaries instead of adding a file-watcher/plugin bus, and checks newly discovered rules before a proposed write. |

This is a behavior/module adaptation, not a full fork of Pi's package manager, configuration or OpenCode's Effect services. Both upstream projects are MIT; exact notices are retained in `Licenses/ProjectReuse/`.

Local policy intentionally differs from upstream: only the selected workspace is searched, using the existing `WorkspaceService.Resolve` boundary injected as a delegate. Symlinks/junctions are rejected. There is no automatic reading of global home configuration or arbitrary remote instructions. Discovery roots are `.agents/skills`, `.cogito/skills`, `.pi/skills`, `.opencode/skills`, in that precedence. Invalid names/frontmatter are diagnosed and skipped rather than loaded with warnings. Skill discovery is limited to 64 skills, 256 visited directories, depth 8, and bounded files; truncation is explicit. This first port does not apply custom `.gitignore`/`.ignore` rules inside these explicit skill roots.

Skills are loaded through `read_skill`, returning exact file hash, body and base directory. `disable-model-invocation: true` skills are excluded from the automatic catalog and require a matching explicit user `/skill:name` token. Loading a skill does not execute its scripts or change approvals. Relative references remain relative to the skill's base directory within the workspace.

## Mature .NET components

Frontmatter parsing uses **YamlDotNet 18.1.0**, MIT, rather than a custom YAML parser. NuGet package metadata records repository revision [`748334a8fa7c227740018b284b71ad95cc6b7fc7`](https://github.com/aaubry/YamlDotNet/tree/748334a8fa7c227740018b284b71ad95cc6b7fc7). Only the YAML DOM and scalar metadata are read; no arbitrary CLR objects are deserialized. The original license is copied alongside the Pi and OpenCode notices.

File streams, UTF-8 decoding, SHA-256, generated regular expressions, JSON source generation, dictionaries and cancellation use .NET BCL. The existing SQLite-backed `ISessionStore` journal persists procedure candidates atomically through its existing event append operation. No additional database schema or runtime service is required.

## Procedure memory and research boundary

`remember_experience` writes a candidate containing a title, explicit preconditions, reusable procedure, path scope and references to actual completed tool events. It requires evidence and rejects assistant claims, skill-loading output and recursive memory output as sources. Identical candidates deduplicate; a changed candidate can explicitly supersede a previous candidate. Entries are additive `experience.saved` events; source sessions and tool observations remain intact.

`recall_project_memory` searches recent sessions belonging to the same canonical workspace, ranks by bounded keyword matching, and rechecks original event/result hashes plus any file hashes reported by known tools. A replacement suppresses an older candidate only when its source records are intact and its timestamp is later, so broken replacements cannot hide valid predecessors or form suppression cycles. A zero command exit does not promote a procedure to Verified. Even matching recorded file hashes covers only those files, not the environment, dependencies or whole build. Returned content is reference data and is never executed automatically or treated as system instructions.

This is a deliberately small application of [ACE's incremental updates](https://arxiv.org/html/2510.04618v2) and [Agentic Plan Caching's conditional procedure reuse](https://proceedings.neurips.cc/paper_files/paper/2025/hash/9549f7d06700f0966d5f938f1d11022a-Abstract-Conference.html). The deterministic source checks are Cogito's engineering layer. We have not implemented the complete Generator/Reflector/Curator algorithm, offline GEPA optimization, learned retrieval or a paper benchmark. There is no claim of improved task success until fixed-budget comparisons are run.

Retrieval bounds the returned records and candidate scan to 64 sessions / 128 candidates. The existing session/event store still reads each selected query into memory, so this is not a claim of fully paginated I/O or constant end-to-end memory use. A single procedure's natural-language interpretation remains Candidate regardless of its source validation state.

Validation lives in `ProjectRulesTests`, `ProjectExperienceTests`, and the engine/Host integration suite. Tests cover scope order, change/removal reload, links, skill parser/discovery compatibility, explicit invocation, durable cross-session retrieval, source/file changes, duplicate/superseding deltas, rejected unsupported evidence and response bounds.
