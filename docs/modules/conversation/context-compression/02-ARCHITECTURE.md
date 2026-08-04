# Context Compression — Architecture

```text
PlatformChatHistory -> MAF ChatMessage history
  -> MafCapabilityProvider
  -> CompactionProvider
       -> SlidingWindow / Summarization / Truncation strategy
  -> Provider IChatClient
```

`MafAgentFactory` 根据 `ContextPolicy` 选择 MAF 策略。摘要策略复用已解析和授权的
`IChatClient`，不会通过第二套 Engine 请求或未授权的模型路径生成摘要。

平台会话存储保留完整审计历史；compaction 只决定本次模型调用看到的消息。
