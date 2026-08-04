# LLM Provider 集成 — 规格说明

## 配置解析

`MafAgentProvider` 读取 `AgentConfig.Llm`，调用 `ILlmRegistry.ResolveConfig` 合并 Profile 与 Agent 局部覆盖，再传给 `IMafChatClientFactory.Create`。

```csharp
internal interface IMafChatClientFactory
{
    IChatClient Create(LlmConfig config);
}
```

必需字段为 `Format`、`ModelId`；三个云 Provider 都需要 API key，Endpoint 为空时 OpenAI 使用官方默认地址，Anthropic 使用 SDK 默认地址。密钥不得进入日志。

## 格式映射

| ApiFormat | SDK 边界 |
|---|---|
| OpenAIChatCompletions | OpenAI Chat client |
| OpenAIResponses | OpenAI Responses client |
| AnthropicMessages | MAF Anthropic provider |

## 失败语义

配置缺失或格式不支持在发出网络请求前失败；Provider HTTP、限流、模型和内容策略错误保持原异常进入平台失败路径。权限校验在 client 请求之前完成。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [03-DESIGN](./03-DESIGN.md)
- [04-TASKS](./04-TASKS.md)
- [05-TESTS](./05-TESTS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
