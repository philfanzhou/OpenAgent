# LLM Provider 集成 — 设计说明

## 数据流

```text
AgentConfig.Llm
  -> ILlmRegistry.ResolveConfig
  -> MafChatClientFactory
       -> protocol-specific official SDK
       -> IChatClient
  -> ChatClientAgent
  -> FunctionInvokingChatClient
```

API 格式按协议分支，不共享自研 HTTP body 或 SSE parser。Responses 使用 Responses client；Anthropic 使用 Messages provider；Gemini 使用 generateContent provider；仅明确兼容 Chat Completions 的端点复用 OpenAI Chat client。

## 配置热更新

`MafAgentProvider` 不缓存 `AIAgent`。每次调用读取当前 Agent 配置并创建轻量 `ChatClientAgent`，从而与现有 ConfigProvider 热更新保持一致。

## 能力边界

平台将图片/PDF/文本、工具 Schema 和历史转换为 MEAI 内容。具体模型是否支持视觉、PDF、函数或 reasoning 由 Provider 返回明确结果；工厂不伪造能力。

## 相关文档

- [01-FEATURE](./01-FEATURE.md)
- [02-SPEC](./02-SPEC.md)
- [04-TASKS](./04-TASKS.md)
- [05-TESTS](./05-TESTS.md)
- [06-CONVENTIONS](./06-CONVENTIONS.md)
