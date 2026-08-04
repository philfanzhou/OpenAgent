# Context Compression — Data Models

平台只保留入口合同 `ContextPolicy`：

| 字段 | 用途 |
|---|---|
| `Strategy` | 选择 MAF compaction strategy |
| `MaxTokens` | token trigger 阈值 |
| `PreserveRecentTurns` | 滑动窗口保留轮次或摘要保留组数 |
| `SummarizeOptions` | 兼容入口字段；摘要执行由 MAF 管理 |

运行时状态、消息分组、trigger 和 target 均使用 MAF
`Microsoft.Agents.AI.Compaction` 类型，不复制平台 DTO。
