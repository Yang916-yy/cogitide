# 文件导航与代码轮廓

| 文件 | 职责 |
| --- | --- |
| [RepositoryInventory.cs](RepositoryInventory.cs) | Git 标准忽略规则下的有界文件清单，及非 Git 目录的明确标记 fallback。 |
| [NavigationTools.cs](NavigationTools.cs) | 文本分页、长行续页、内容快照、字面/正则/glob 搜索和上下文结果。 |
| [CSharpSymbols.cs](CSharpSymbols.cs) | Roslyn C# 语法声明、签名、行号和诊断，不提供跨项目语义分析。 |

读取/检索/glob 的产品行为参考 **OpenCode commit `60072747957926e7aeec7e911fe6e23abe9bf830`**，固定源文件、差异及 MIT 许可见 [Navigation provenance](../Licenses/Navigation/PROVENANCE.md)。本模块用 C# 组件改编所需行为，没有复制 OpenCode 的 TypeScript 文件系统或事件框架。

glob 复用 **Microsoft.Extensions.FileSystemGlobbing 10.0.14**，正则复用 BCL `Regex` 并设每行匹配超时，C# 语法复用 **Microsoft.CodeAnalysis.CSharp 5.9.0**；Git 提供标准 ignore 结果，文件/JSON/哈希/取消复用 .NET。精确依赖及资源预算见 [DEPENDENCIES](../DEPENDENCIES.md)。分页快照不是跨所有文件的 OS 原子快照，Roslyn 轮廓也不等于编译或测试成功。

搜索后按需精读支持 DCI 式直接语料交互，但本目录主要是成熟工具基础；不能把存在 grep/glob 就写成完整 DCI 论文复现。历史定位另见 [RepositoryMemory](../RepositoryMemory/README.md)。

**严禁重复造不必要的轮子。** 禁止手写正则、glob、gitignore 或 C# parser；首先复用上述组件与统一路径边界。新检索层须证明现有搜索的具体不足，记录精确来源/许可，并提供同输入预算的结果和性能对照。
