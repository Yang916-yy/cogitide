# Cogitide repository instructions

These rules apply throughout this repository. More local module README files provide provenance and technical constraints; read them before changing their code.

## Product and architecture

- Build a coding-first desktop agent in C# 15 / .NET 11. Desktop, stdio Host and all model providers share the same agent kernel. Account login supplies authentication only.
- Retain `Workbench` assembly names and data paths for compatibility. The public product name is Cogitide.
- Runtime research features are explicit opt-ins. Offline optimization must never silently spend money, activate a candidate, grow a budget, or use final-test feedback to choose candidates.

## Reuse is mandatory

**严禁重复造不必要的轮子。** Before adding infrastructure, inspect .NET/BCL, the existing adapters and mature upstream implementations. Prefer existing official SDKs, `SocketsHttpHandler`, System.Text.Json, SQLite, Git, Roslyn and Avalonia to parallel home-made equivalents. Port only needed behavior from a fixed upstream revision; preserve its license and record changes. New custom infrastructure requires a concrete missing capability, comparison with existing choices, and validation evidence in the PR. This is a design requirement, not an extra human-approval gate for routine authorized work.

- Peripheral module READMEs must identify which code is adapted, which behavior is merely referenced, and which C# components are reused. Never imply a line-for-line port when only behavior was borrowed.
- Kernel module READMEs must cite primary papers, identify the implemented mechanism, describe project additions, and separate functional tests from measured research benefits. Do not call an engineering combination a proven academic innovation.
- Do not write a second HTTP/SSE stack, parser, filesystem matcher, process scheduler, crypto implementation or storage layer when the current mature component meets the requirement.

## Execution and verification

- Use the SDK pinned in `global.json`; serialize restore/build/test/publish commands sharing `obj/`. Run tests in the background and do not open the desktop unless explicitly requested.
- No secrets, local account stores, SDK binaries, runtime databases, personal workspaces or raw local artifacts in Git. Keys come from environment/system credential storage.
- Preserve approval checks, project rules, full evidence and unknown-outcome states. Current execution modes are application permissions, not an OS sandbox.
- Budget or cancellation must not commit incomplete model state, replay old actions or mark unobserved commands successful. Dispose owned processes on Host loss.
- Validate relevant behavior with meaningful tests. Run `scripts/build.ps1`, local integration fixtures and `python scripts/module-map.py --check` before publication. Paid model benchmarks require an explicitly authorized budget; deterministic fixtures are the default.
- Update the generated module map when adding/moving files: `python scripts/module-map.py`. Keep documentation links and third-party notices valid. Preserve history rather than rewriting old validation results as current claims.
