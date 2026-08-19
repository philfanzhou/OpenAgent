# Streaming Execution

流式推理输出通过 `IAsyncEnumerable<string>` 持续产出模型内容，支持工具调用中间状态透传、取消信号传播和异常向上报告。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 流式输出 | 持续 `yield return` 内容片段 |
| 工具调用中间状态 | 产出 `ToolCall` 类型 `AgentStreamEvent`，含 `ToolName`/`ToolCallId`/`ToolArguments` |
| ToolCall 去重 | 工具调用事件按 `CallId`/`Name` 去重后广播 |
| 取消/失败写回 | 取消和异常时写回 partial 消息并标记状态 |
| 终态用量 | Provider usage 只随 `done` SSE 事件发送；缺失时为 `null` |
| 中间件链 | 流式路径经过完整中间件链 |

## Architecture
```text
AgentExecutor.ExecuteStreamingAsync
  → AIAgent.RunStreamingAsync
  → ChatClientAgent.RunStreamingAsync
  → AgentResponseUpdate（MAF 原生）
  → assistant chunks / tool markers / usage
```

## Current Status
**Implemented** — 流式输出、ToolCall 合并、取消/失败写回均已落地。

## Limits
- SSE/NDJSON 协议格式化属宿主层，Core 不负责
- ReasoningContent 作为 `Reasoning` 类型 `AgentStreamEvent` 产出给消费者
- 取消/失败写回使用 `CancellationToken.None` 确保 partial 消息不丢失
- 不根据流式文本片段估算 Token；终态没有完整 usage 时保持不可用

## Source
- Core: `Backend/src/OpenAgent.Core/Runtime/Agent/`
- Core tests: `Backend/tests/OpenAgent.Core.Tests/Runtime/AgentExecutorUsageTests.cs`
- SSE tests: `Backend/tests/OpenAgent.Engine.Tests/Hosting/AgentStreamWriterTests.cs`
