# 系统保护凭据存储

[ProtectedAuthStore.cs](ProtectedAuthStore.cs) 保存本应用独立注册的账号凭据与非秘密元数据，提供实例隔离、存储生命周期及提交边界。用户会话和代码历史另存于 [Core/Persistence](../../Workbench.Core/Persistence/README.md)，不要把二者合成一个未经区分的 JSON 文件。

保护能力复用 **Microsoft.Identity.Client.Extensions.Msal 4.92.0** 的公开 `Storage` API：Windows DPAPI、macOS Keychain、Linux libsecret/Secret Service。Windows 的 `ProtectedData` 使用固定 **10.0.14** 依赖。MSAL 在这里用于成熟系统存储，不是 Microsoft 账号鉴权。

上游存储组件不提供所需的跨进程互斥，因此本模块只补充 BCL 排他文件句柄和按数据目录分隔的 vault 标识；Windows 用保护后的临时文件与替换提交完整记录。精确源 commit、MIT notices、平台边界见 [Auth provenance](../Licenses/PROVENANCE.md)。系统凭据服务不可用会明确失败，不切换明文保存。

**严禁重复造不必要的轮子。** 不得编写自制密码库、令牌加密算法、Keychain/libsecret 包装服务或新的明文 fallback；首先复用 Microsoft 组件和平台能力。确有平台缺口时在 PR 提供系统环境、最小复现、替代组件及迁移/损坏恢复测试，不把跨平台编译当作 vault 实机通过。
