**English** | [简体中文](README.zh-CN.md)

# Project rules and local skill discovery

`ProjectRules.cs` loads scoped ancestor `AGENTS.md` files, detects changes, discovers local skills, parses frontmatter, handles explicit invocation and reads selected bodies. It neither installs remote skills nor executes their scripts or grants tool permissions.

Pi commit `e7a0df27c09d11bf4161d3605246cbf57a129cb2` supplies referenced/adapted `skills.ts` discovery behavior: directory termination, catalog before bodies, names/duplicates and explicit invocation. OpenCode `60072747957926e7aeec7e911fe6e23abe9bf830` supplies ancestor instructions, scope and reinjection on context rebuild. [PROJECT-REUSE](../PROJECT-REUSE.md) and [ProjectReuse notices](../Licenses/ProjectReuse/) distinguish exact function mappings/differences and preserve MIT notices. This is not either project's complete plugin manager.

**YamlDotNet 18.1.0** reads DOM scalar metadata without arbitrary CLR deserialization. Paths reuse injected `WorkspaceService.Resolve`; hashes, file streams and cancellation reuse BCL. Discovery is limited to the chosen workspace with directory/depth/count/text bounds. Reading a skill never executes its commands or expands permissions. Model generated candidate experience stays in [Memory](../Memory/README.md), without writing `AGENTS.md` to evade candidate status.

**Do not reinvent infrastructure.** Extend this discovery layer, YamlDotNet and existing paths instead of adding a YAML/frontmatter parser or parallel rule/skill service. Necessary changes need source/compatibility/scope evidence, without new inferred approval flows.

This English page summarizes the preserved source. [The full Chinese record](README.zh-CN.md) retains complete historical details, examples, evidence paths and qualifications. Historical dates and test counts above are not new verification claims.

## Preserved source references

- [Original reference 1](ProjectRules.cs)
