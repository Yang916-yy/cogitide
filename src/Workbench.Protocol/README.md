# 共享协议与数据契约

| 文件 | 职责 |
| --- | --- |
| [Contracts.cs](Contracts.cs) | JSONL RPC、会话、消息、工具、运行选项、账号/模型状态及服务接口；研究功能以显式 `ResearchOptions` 控制。 |
| [CompactionContracts.cs](CompactionContracts.cs) | 带覆盖边界、来源摘要和版本的 checkpoint 数据契约。 |
| [WireJsonContext.cs](WireJsonContext.cs) | 稳定 wire/storage DTO 的 System.Text.Json 源生成元数据。 |
| [ImageInputs.cs](ImageInputs.cs) | 图片输入数量、格式签名、base64 和大小预算检查。 |

这是 Cogitide 自己的轻量进程间协议，不声称与 Codex/OpenCode 的全部协议兼容。边界和结构借鉴成熟 Host/客户端分离方式；JSON、base64、序列化元数据和密码摘要等基础算法使用 .NET/BCL，动态/匿名 payload 保留必要的兼容回退，不另外实现解析器。

原生 provider continuation 是不透明、绑定原端点/模型的数据；它不等于账号令牌，不得因序列化方便混存授权秘密。新字段应保持兼容或明确升级版本，未知状态不能降级成成功；研究开关默认关闭，正常工具契约不承担自动启动离线训练的授权。

**严禁重复造不必要的轮子。** 共享 DTO 和接口必须扩展本项目契约，不得在 GUI、Host、研究 CLI 中分别复制一份；禁止自行实现 JSON/base64 协议栈。新增序列化依赖须在 PR 说明现有 System.Text.Json 的实际限制、迁移方式、旧数据兼容及错误边界测试。
