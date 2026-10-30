# Contributing

Read [AGENTS.md](AGENTS.md) and the README in the module being changed. The public name is Cogitide; internal namespaces, assembly names and data paths remain Workbench.

**严禁重复造不必要的轮子。** Start by checking .NET/BCL, existing project components, official SDKs, then focused adaptations of mature upstream projects. If custom code is necessary, explain the concrete gap and alternatives. Keep the smallest implementation that serves the product. Record fixed source revisions and retain all original notices for adapted code.

Use the pinned SDK through `scripts/build.ps1`. Python 3 and Git are needed for offline integration fixtures. Do not run overlapping SDK commands against the same checkout. Tests should run without opening windows or accessing real model accounts. Explicit live flags in the older scripts consume API usage and are not part of default verification.

A PR should describe the user-visible change, relevant validation, source/stock-component reuse and remaining limits. For kernel changes include the primary paper, implemented portion, project-specific changes and an ablation plan. Functional fixture results do not establish benchmark superiority. Preserve train/selection/holdout separation, immutable validation and the baseline implementation.

Run `python scripts/module-map.py` after adding/moving files. Public documents must use repository-relative links and portable examples. Never commit API keys, account profiles, local user directories, downloaded SDKs, build output or personal test data. Dependencies must be pinned in project lock files and carry compatible redistribution notices.

Contributions are provided under this repository's Apache-2.0 license; upstream portions retain their existing licenses. No contributor license agreement or claimed transfer of upstream rights is implied.
