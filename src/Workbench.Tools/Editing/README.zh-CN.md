[English](README.md) | **简体中文**

# 多文件补丁

[PatchTools.cs](PatchTools.cs) 为 `WorkspaceService` 的 partial 扩展，实现 `apply_patch` 的 Add/Update/Delete/Move、上下文 hunk 和末尾定位。源文件、移动目标、哈希与上下文先统一预检，再提交；审批和项目规则检查使用同一个解析出的路径集合。

补丁格式与解析框架**改编自 OpenCode v1.19.5、commit `810c0eba1a7348036f8bdf61e6632d0c36c6876c` 的 `packages/opencode/src/patch/index.ts`**。具体移植范围、原始 MIT 许可和有意差异见 [Patch provenance](../Licenses/Patch/PROVENANCE.md)。现有源文件必须有 SHA-256，匹配要求精确且唯一，不采用模糊空白/标点兜底猜测。

文件流、路径、字节编码、哈希、锁、取消和临时文件使用 .NET/BCL 与 [WorkspaceService](../Workspace/README.md)。现有 BOM、换行和可用平台权限保持；受控提交失败尝试回滚，不能安全恢复时保留明确恢复材料。多文件操作不是断电/系统崩溃时的原子事务。

**严禁重复造不必要的轮子。** 不得再写另一套同格式 patch parser、哈希编辑器或 Git diff 引擎；先复用本模块、上游成熟实现或必要的标准组件。新增兼容行为必须说明对应源版本/许可证、歧义处理、回滚及规则范围测试，不能以方便模型为由取消内容版本校验。
