# Skill catalog reference provenance

No upstream skill bodies, scripts, installer code or plugin implementation are bundled with this adapter. `SkillManagementService` is original C# adapter code using existing Core parser/workspace behavior and .NET BCL networking/ZIP primitives. The following repositories are remote data sources only, addressed by immutable full Git commits:

| Source | Revision and reference | License handling |
| --- | --- | --- |
| Anthropic examples (default) | [`ca45fc496a3436251f191354091d557da31c15b5`](https://github.com/anthropics/skills/tree/ca45fc496a3436251f191354091d557da31c15b5), `skills/*/SKILL.md` | The upstream README distinguishes Apache-2.0 examples from source-available document skills. Preserve each selected package's own `LICENSE*`; do not infer a universal license. Repository `THIRD_PARTY_NOTICES.md` is retained too. |
| OpenAI legacy catalog (optional) | [`49f948faa9258a0c61caceaf225e179651397431`](https://github.com/openai/skills/tree/49f948faa9258a0c61caceaf225e179651397431), `skills/.curated/*/SKILL.md` | The upstream README locates licenses in each skill directory and marks this catalog deprecated. Preserve the exact selected package's `LICENSE*` and root notices. |

Anonymous immutable archives are fetched from `https://codeload.github.com/<owner>/<repository>/zip/<full-commit>`. Installation copies the selected directory unchanged plus repository root `LICENSE*`, `NOTICE*`, and `THIRD_PARTY_NOTICES.md` into `.cogitide-source-notices`. A package lacking a license file is refused by online install. User-provided local imports preserve the files the user explicitly selected. `.cogitide-skill.json` records exact origin/revision/path and original per-file hashes; it is not a license grant or vendor verification.

Core's pre-existing Pi/OpenCode discovery references and YamlDotNet license are maintained in [PROJECT-REUSE](../../../Workbench.Core/PROJECT-REUSE.md) and its license directory. This adapter does not claim to port Pi, OpenCode, Claude, Codex, or either upstream skill installer.
