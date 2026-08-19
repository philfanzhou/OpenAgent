
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
  -> CapabilityToolFactory
  -> CompactionProvider
       -> SlidingWindow / Summarization / Truncation strategy
  -> Provider IChatClient
```

`AgentFactory`（经 `ConversationHistoryFactory.CreateCompaction`）根据 `ContextPolicy` 选择 MAF 策略。摘要策略复用已解析和授权的
`IChatClient`，不会通过第二套 Engine 请求或未授权的模型路径生成摘要。

平台会话存储保留完整审计历史；compaction 只决定本次模型调用看到的消息。

自动压缩通过审计包装记录触发方式、策略、原始消息范围、摘要或结果以及失败恢复状态，包装层不实现消息裁剪。
`POST /api/v1/agent/conversations/{conversationId}/compact` 在校验租户、用户、会话归属和 Agent 授权后，使用同一 MAF
策略执行手动压缩。压缩失败时恢复调用前的消息组，完整会话消息不会被覆盖或删除。
最近一次成功的手动压缩结果作为后续模型调用的历史视图，并自动拼接压缩后新增的原始消息；自动压缩结果仍只作用于触发它的 Agent run。

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

每次实际自动压缩和每次手动尝试以 `ContextSummary` 追加到会话的数据库记录。Chat Inspector 展示压缩次数、最近策略、
触发方式、原始范围、摘要或结果；失败记录同时标识原始上下文是否已恢复。
