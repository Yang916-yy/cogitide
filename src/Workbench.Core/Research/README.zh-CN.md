[English](README.md) | **简体中文**

# Research：可关闭或显式启动的核心增强

- [SkillOptimization](SkillOptimization/README.md)：GEPA/SkillOpt 的离线技能搜索，冻结选择后才评估最终留出集，不自动安装技能。
- [Speculation](Speculation/README.md)：下一次文件读取与模型推理重叠，实际请求匹配和文件哈希核对后才使用。
- ACON 压缩与指南优化复用既有摘要模块，位于 [Context](../Context/README.md)。

算法只负责策略与结果；模型、工具、进程和状态继续复用主内核及成熟 .NET 组件。**严禁重复造不必要的轮子。** 具体论文、工程改造、边界与测试在子目录 README；功能通过不等于论文收益复现。
