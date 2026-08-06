
## Feature


上下文压缩由 Microsoft Agent Framework `CompactionProvider` 执行，不再维护平台压缩器。

支持的 `ContextPolicy.Strategy`：

- `sliding_window` → `SlidingWindowCompactionStrategy`
- `summarize` → `SummarizationCompactionStrategy`
- `none` / 未配置 → 按平台历史上限使用 `TruncationCompactionStrategy`

压缩发生在 MAF 模型调用前，保持函数调用与函数结果的原子消息组，并可覆盖同一 Agent
run 内的后续工具迭代。

## Architecture


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

## Data Models


`ContextPolicy` 是 `AgentConfig` 的一部分，由 `IAgentRuntimeResolver` 从 Agent Profile
解析后提供给本次运行；客户端请求不再覆盖该策略：

| 字段 | 用途 |
|---|---|
| `Strategy` | 选择 MAF compaction strategy |
| `MaxTokens` | token trigger 阈值 |
| `PreserveRecentTurns` | 滑动窗口保留轮次或摘要保留组数 |
| `SummarizeOptions` | 兼容入口字段；摘要执行由 MAF 管理 |

运行时状态、消息分组、trigger 和 target 均使用 MAF
`Microsoft.Agents.AI.Compaction` 类型，不复制平台 DTO。
