"""Generate/check the repository's module inventory, without build artifacts."""
from pathlib import Path
import argparse

ROOT = Path(__file__).resolve().parents[1]
EXCLUDED = {"bin", "obj", "__pycache__", ".git", ".tools", "artifacts", "TestResults"}


def files(folder):
    return sorted(p for p in folder.rglob("*") if p.is_file() and not EXCLUDED.intersection(p.relative_to(ROOT).parts))


def inventory():
    lines = ["# 模块目录与文件数", "", "由 `python scripts/module-map.py` 生成；`--check` 检查是否过期。",
             "统计仓库内文件，排除构建产物/SDK/本地运行记录。子模块行是项目总计的子集，不可再次相加。", "",
             "| 模块 | 代码 .cs | UI .axaml | Markdown | 全部文件 |", "| --- | ---: | ---: | ---: | ---: |"]

    def row(folder):
        entries = files(folder)
        if not entries:
            return
        relative = folder.relative_to(ROOT).as_posix()
        readme = folder / "README.md"
        target = "../" + relative + ("/README.md" if readme.exists() else "/")
        counts = [sum(p.suffix == suffix for p in entries) for suffix in (".cs", ".axaml", ".md")]
        lines.append(f"| [{relative}]({target}) | {' | '.join(map(str, counts))} | {len(entries)} |")

    for project in sorted((ROOT / "src").iterdir()):
        if not project.is_dir():
            continue
        row(project)
        for module in sorted(project.iterdir()):
            if module.is_dir() and module.name not in EXCLUDED | {"Licenses"}:
                row(module)
                if module.name == "Research":
                    for submodule in sorted(module.iterdir()):
                        if submodule.is_dir():
                            row(submodule)
    for name in ("tests", "scripts", "docs", "research", ".github"):
        row(ROOT / name)
    lines.extend(["", "外围模块优先复用 .NET/BCL 与成熟 SDK；具体改编边界见就近 README/PROVENANCE。",
                  "内核论文机制和项目增加部分见 Context、Memory、Research、RepositoryMemory 的 README。",
                  "生成文件自身计入 docs；原始上游许可证计入所属项目总计。", ""])
    return "\n".join(lines)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    destination = ROOT / "docs/module-map.md"
    if not args.check:
        destination.touch(exist_ok=True)
    content = inventory()
    if args.check:
        if not destination.exists() or destination.read_text(encoding="utf-8") != content:
            raise SystemExit("Module map is stale: run python scripts/module-map.py")
        print("Module map is current.")
    else:
        destination.write_text(content, encoding="utf-8")
        print("Wrote docs/module-map.md")
