# Auth：官方账号鉴权

[Accounts](Accounts/README.md) 按官方公开 OAuth 契约管理账号，复用 .NET HTTP 与 Microsoft JWT 组件；[Storage](Storage/README.md) 使用系统保护存储。登录仅提供模型鉴权，不委托外部 agent 执行。程序不读取其他应用的账号文件。

来源、依赖和原始告知见 [Licenses/PROVENANCE.md](Licenses/PROVENANCE.md)。公开产品名为 Cogitide；既有系统存储服务名保持兼容。真实登录与三平台系统凭据服务仍需实机验证。

**严禁重复造不必要的轮子。** 不另写密码学、JWT 验签器或系统密钥库；先使用成熟 .NET 组件。不存在公开官方协议的厂商只使用 API，不模拟私有订阅登录。
