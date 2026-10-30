# 模块目录与文件数

由 `python scripts/module-map.py` 生成；`--check` 检查是否过期。
统计仓库内文件，排除构建产物/SDK/本地运行记录。子模块行是项目总计的子集，不可再次相加。

| 模块 | 代码 .cs | UI .axaml | Markdown | 全部文件 |
| --- | ---: | ---: | ---: | ---: |
| [src/Workbench.Auth](../src/Workbench.Auth/README.md) | 3 | 0 | 4 | 12 |
| [src/Workbench.Auth/Accounts](../src/Workbench.Auth/Accounts/README.md) | 2 | 0 | 1 | 3 |
| [src/Workbench.Auth/Storage](../src/Workbench.Auth/Storage/README.md) | 1 | 0 | 1 | 2 |
| [src/Workbench.Core](../src/Workbench.Core/README.md) | 20 | 0 | 15 | 45 |
| [src/Workbench.Core/Context](../src/Workbench.Core/Context/README.md) | 3 | 0 | 1 | 4 |
| [src/Workbench.Core/Memory](../src/Workbench.Core/Memory/README.md) | 4 | 0 | 1 | 5 |
| [src/Workbench.Core/Models](../src/Workbench.Core/Models/README.md) | 6 | 0 | 1 | 7 |
| [src/Workbench.Core/Persistence](../src/Workbench.Core/Persistence/README.md) | 1 | 0 | 1 | 2 |
| [src/Workbench.Core/Projects](../src/Workbench.Core/Projects/README.md) | 1 | 0 | 1 | 2 |
| [src/Workbench.Core/Research](../src/Workbench.Core/Research/README.md) | 4 | 0 | 3 | 7 |
| [src/Workbench.Core/Research/SkillOptimization](../src/Workbench.Core/Research/SkillOptimization/README.md) | 3 | 0 | 1 | 4 |
| [src/Workbench.Core/Research/Speculation](../src/Workbench.Core/Research/Speculation/README.md) | 1 | 0 | 1 | 2 |
| [src/Workbench.Core/Runtime](../src/Workbench.Core/Runtime/README.md) | 1 | 0 | 1 | 2 |
| [src/Workbench.Desktop](../src/Workbench.Desktop/README.md) | 17 | 3 | 2 | 26 |
| [src/Workbench.Desktop/Resources](../src/Workbench.Desktop/Resources/) | 0 | 0 | 0 | 2 |
| [src/Workbench.Host](../src/Workbench.Host/README.md) | 2 | 0 | 2 | 6 |
| [src/Workbench.Host/Research](../src/Workbench.Host/Research/README.md) | 1 | 0 | 1 | 2 |
| [src/Workbench.Protocol](../src/Workbench.Protocol/README.md) | 4 | 0 | 1 | 7 |
| [src/Workbench.Tools](../src/Workbench.Tools/README.md) | 9 | 0 | 10 | 27 |
| [src/Workbench.Tools/Editing](../src/Workbench.Tools/Editing/README.md) | 1 | 0 | 1 | 2 |
| [src/Workbench.Tools/Execution](../src/Workbench.Tools/Execution/README.md) | 1 | 0 | 1 | 2 |
| [src/Workbench.Tools/Navigation](../src/Workbench.Tools/Navigation/README.md) | 3 | 0 | 1 | 4 |
| [src/Workbench.Tools/RepositoryMemory](../src/Workbench.Tools/RepositoryMemory/README.md) | 2 | 0 | 1 | 3 |
| [src/Workbench.Tools/Workspace](../src/Workbench.Tools/Workspace/README.md) | 1 | 0 | 1 | 2 |
| [tests](../tests/) | 35 | 1 | 0 | 38 |
| [scripts](../scripts/) | 0 | 0 | 0 | 15 |
| [docs](../docs/) | 0 | 0 | 8 | 8 |
| [research](../research/) | 0 | 0 | 8 | 8 |
| [.github](../.github/) | 0 | 0 | 0 | 1 |

外围模块优先复用 .NET/BCL 与成熟 SDK；具体改编边界见就近 README/PROVENANCE。
内核论文机制和项目增加部分见 Context、Memory、Research、RepositoryMemory 的 README。
生成文件自身计入 docs；原始上游许可证计入所属项目总计。
