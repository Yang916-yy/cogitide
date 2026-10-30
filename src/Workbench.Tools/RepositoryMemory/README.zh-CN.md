[English](README.md) | **简体中文**

# Repository memory：版本冻结的历史定位

这是可选的 `repository_memory` 只读工具，不是完整 RepoMem/LocAgent 或 DCI-Agent 的复刻。启用时，Host 在每次任务开始只解析一次完整 Git commit ID，后续查询不会追随新提交。Git 不可用、没有提交或仓库损坏时明确报错，不扫描替代目录、不拉取远程。

## 论文思想与本项目实现

- [Improving Code Localization with Repository Memory，v2](https://arxiv.org/html/2510.01003v2)：采用从提交历史获取定位线索的 episodic memory 思路。这里按提交说明、路径关键词和已有线索文件的共同修改记录排序；未实现论文的 LLM 功能摘要、学习检索器、语义记忆和 LocAgent 图。
- [Direct Corpus Interaction](https://arxiv.org/html/2605.05242)：采用直接检索原始材料、展示来源、再由 agent 缩小范围的交互方式。[作者 DCI-Agent-Lite](https://github.com/DCI-Agent/DCI-Agent-Lite) 已核对，但没有复制其提示词或 Python 代码。当前 agent 可组合此工具与现有 `search_files`、`read_file`、`inspect_evidence`；没有移植作者 harness、训练或论文评测。
- 本项目新增的工程组合：任务内冻结 ancestry 边界；只返回 base 中的常规 blob 路径；结果携带完整 source commit、base blob ID、相关原始片段、明确分项得分与当前文件存在性；分页以请求和结果快照校验。它们是可检查的设计差异，不是已证明的新算法贡献或论文收益。

输入 `seedPaths` 可在没有查询词时寻找共同修改的候选文件。指定 `pathPrefix` 时种子也须位于该范围，跨目录关联应扩大范围。评分为路径匹配每项 4 分、提交说明匹配每项 3 分（最多 24 项）、共同修改提交每次 6 分（最多 8 次），只是启发式顺序，不是概率。共同修改不证明因果；消息中的指令永远是无权限历史数据。

## 组件复用与贡献规则

本模块独立实现产品策略，**未复制研究作者代码**。复用已有 `WorkspaceService.ReadGitAsync` 与工作区边界检查；Git CLI 提供 revision 解析、祖先判断、树对象、提交元数据和 `--numstat -z`，避免自行解析 Git 对象、pack 或重写历史索引。元数据和 diff 分开读取，防止提交说明伪装成变更路径。关闭 replace objects、可选锁、external diff、textconv；通过参数列表调用 Git，不经过 shell。

C#/.NET 负责 `Process`、异步流、取消令牌、`SemaphoreSlim`、SHA-256、字典和 `System.Text.Json`。不新增服务、模型调用、向量数据库或自制正则/JSON/进程运行时。**严禁后续贡献者重复造不必要的轮子**：新增依赖或自建基础设施前，应说明现有 BCL、Git CLI 或已锁定库无法满足的具体契约；代码移植必须固定源版本并保留相应版权及许可证。

每个工具实例只缓存最近一次不可变历史语料，查询及当前文件存在性仍重新计算。不扫描未跟踪文件，不读取工作区外或 Git 内部路径。历史内已删除且 base 中不存在的路径不作为当前修复候选；基于 Git numstat 标记的二进制变更、base 树中的 symlink/submodule 和当前路径链接被排除。未跟随重命名前的文件身份，也不声称所有文件当前内容都已验证。

资源上限：默认 32、最多 64 个提交；每提交最多 128 个合格文本变更；base 树最多 20,000 个文件/2,000,000 字符；历史元数据合计约 4,000,000 字符，单个 Git 输出最多 65,536 字符；整体扫描 25 秒并沿用单 Git 20 秒超时。达到预算时明确报告 `historyTruncated`，应缩小范围；单个输出过大的提交跳过并计数。每页最多 50 个候选、每候选最多 4 条来源，最终 JSON 不超过 80,000 字符。消息片段给出字符偏移及总长度，不把片段当完整消息。

`RepositoryMemoryTests` 使用真实临时 Git 仓库检查未来提交泄漏、冻结 base、不可达版本拒绝、共同修改排序、子目录隔离、UTF-8 路径、历史消息伪造、二进制与链接过滤、分页失效、取消、无仓库回退及工作树保持。本模块没有执行付费模型实验；单元/集成通过只能证明契约，不能证明 SWE-bench 修复率改善。
