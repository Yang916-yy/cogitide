[English](README.md) | **简体中文**

# 模型外围适配

本目录把供应商 API 映射到统一的 `IAgentModel`。它负责请求、流式响应和原生 continuation，不执行 agent 工具，也不负责项目经验或任务审批。

| 文件 | 职责 |
| --- | --- |
| [AgentModelFactory.cs](AgentModelFactory.cs) | Host 与离线评测共用的 API 模型构造入口。 |
| [OpenAiCompatibleModel.cs](OpenAiCompatibleModel.cs) | 官方 OpenAI .NET SDK Chat Completions 适配及 DeepSeek 原生推理字段兼容。 |
| [OpenAiResponsesModel.cs](OpenAiResponsesModel.cs) | 官方 Responses SDK、完整输出项/加密 reasoning 回放、namespace 和完整终态检查。 |
| [AnthropicModel.cs](AnthropicModel.cs) | 官方 Anthropic SDK Messages、thinking、签名内容块、图片及工具协议。 |
| [ModelCapabilities.cs](ModelCapabilities.cs) | 提供方、端点、图片、思考档位及 continuation 绑定的预检查。 |
| [ModelHttpTransport.cs](ModelHttpTransport.cs) | 共用 BCL HTTP 连接池及连接/重定向/重试策略。 |

## 来源与复用

网络和 SSE 由官方 **OpenAI .NET 2.14.0**、**Anthropic 13.3.0** 和 SDK HTTP pipeline 处理，应用复用 .NET `SocketsHttpHandler`，没有自制 HTTP/SSE 解析器。精确依赖与限制见 [依赖记录](../DEPENDENCIES.md)、[Anthropic 记录](../ANTHROPIC.md) 和项目 `packages.lock.json`。

DeepSeek 缺失 reasoning 的回填行为改编自 Pi commit `f09d01b85e0384647fcab6779d35ab9dabe04be3`；Responses 状态回放参考 Pi commit `e7a0df27c09d11bf4161d3605246cbf57a129cb2`，具体源文件与保留许可证见 [Responses provenance](../Licenses/Responses/PROVENANCE.md)。只移植这些行为，没有复制 Pi 的网络栈、全部模型目录、定价或完整 provider 包。

私有 reasoning 不渲染成回答正文，不改写其签名/opaque 状态；端点或模型变化时不误传旧状态。未完成、截断或失败响应不能提交工具调用。模型调用不隐式重放。账号鉴权属于独立 [Auth](../../Workbench.Auth/Accounts/README.md) 模块，官方账号登录不意味着委托外部 agent 执行。

**严禁重复造不必要的轮子。** 新增供应商首先采用官方或成熟 C# SDK，复用公共网络池、图片检查与已有事件契约；不得仅为包裹另一个名称而重写 SSE、HTTP、JSON、重试器或重复维护一套模型循环。必要的新适配代码须在 PR 说明 SDK 缺口、版本、协议证据和序列化/终态测试。模型支持与账号真实权限分别验收，不将本地协议夹具当在线调用成功。
