# 官方账号授权外围

| 文件 | 职责 |
| --- | --- |
| [IChatGptAccountService.cs](IChatGptAccountService.cs) | 账号状态、登录、选择、退出、凭据与模型目录接口。 |
| [ChatGptAccountService.cs](ChatGptAccountService.cs) | 官方动态 OAuth 注册/回调、state/nonce/PKCE、账号绑定、刷新、撤销与目录获取。 |

本模块按官方公开账号协议实现集成，具体文档、核查日期和限制见 [Auth provenance](../Licenses/PROVENANCE.md)。它不是从其他应用抽取登录文件，也不使用借来的 Codex client ID、私有后端或外部 agent 执行器。授权只给统一的 [Models](../../Workbench.Core/Models/README.md) 提供推理鉴权，工具仍由我们的内核负责。

JWT 签名、issuer、audience 与有效期校验复用 **Microsoft.IdentityModel.JsonWebTokens 8.23.0**；HTTP/TLS、loopback、随机数、SHA-256、固定时间比较、JSON 和取消复用 .NET。应用仅增加协议约束及账号绑定判断，不手写 JWT 密码学。系统令牌保护由 [Storage](../Storage/README.md) 负责。

本地协议夹具和系统存储测试不能证明某个真实账号有模型权限。登录地址由明确请求登录的 GUI 使用；服务自身不擅自打开浏览器。原生 reasoning、任务执行和技能优化不属于此层。

**严禁重复造不必要的轮子。** 禁止重新实现 JWT、TLS、HTTP 或系统凭据库；优先使用已采用的 Microsoft/.NET 组件和官方协议。扩展厂商登录必须有对应公开支持的协议、明确来源和契约测试，不能以改编开源代码为理由引入私有授权捷径或明文令牌回退。
