# 项目规则与本地技能发现

[ProjectRules.cs](ProjectRules.cs) 负责工作区内 `AGENTS.md` 的祖先作用域加载、文件版本变化、skills 目录发现、frontmatter、显式技能调用和正文读取。本目录不安装远程技能，不执行技能脚本，也不管理 agent 的工具权限。

## 改编来源

- **Pi commit `e7a0df27c09d11bf4161d3605246cbf57a129cb2`**：`skills.ts` 中目录递归终止、先目录后正文、技能命名/重复和显式调用等发现行为。
- **OpenCode commit `60072747957926e7aeec7e911fe6e23abe9bf830`**：祖先指令发现、作用域和上下文重建时重新注入规则的行为。
- 具体函数映射、刻意差异和原始 MIT 许可见 [PROJECT-REUSE](../PROJECT-REUSE.md) 与 [ProjectReuse notices](../Licenses/ProjectReuse/)；不是两项目完整插件系统或包管理器的翻译。

YAML frontmatter 使用 **YamlDotNet 18.1.0** 的 DOM/标量读取，不反序列化任意 CLR 对象。路径边界复用调用方注入的 `WorkspaceService.Resolve`，哈希、文件流与取消复用 .NET。发现限定在选定工作区，目录/深度/数量和文本大小有上限；读取技能不会自动执行其命令或扩大权限。

此模块主要是成熟外围能力。模型生成的项目经验属于 [Memory](../Memory/README.md)，不得写回 `AGENTS.md` 以绕过其候选身份。

**严禁重复造不必要的轮子。** 禁止新增自制 YAML/Markdown frontmatter 解析器、第二套规则发现或技能执行服务。优先扩展本模块、YamlDotNet 和已有路径工具；无法复用时在 PR 记录明确缺口、来源、兼容性和规则作用域测试，不引入无依据的审批流程。
