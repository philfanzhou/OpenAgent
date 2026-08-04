# Streaming Execution

流式推理输出通过 `IAsyncEnumerable<string>` 持续产出模型内容，支持工具调用中间状态透传、取消信号传播和异常向上报告。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 流式输出 | 持续 `yield return` 内容片段 |
| 工具调用中间状态 | 工具调用前输出 `\n[Calling tool: {toolName}]\n` |
| ToolCall 合并 | 流式 chunk 中的 ToolCall 片段通过 `MergeToolCalls` 逐步合并 |
| 取消/失败写回 | 取消和异常时写回 partial 消息并标记状态 |
| 中间件链 | 流式路径经过完整中间件链 |

## Architecture
```text
Pipeline.ExecuteStreamAsync
  → AgentRun.RunStreamingAsync
  → ChatClientAgent.RunStreamingAsync
  → AgentResponseUpdate + MafResponseReader
  → assistant chunks / tool markers / usage
```

## Current Status
**Implemented** — 流式输出、ToolCall 合并、取消/失败写回均已落地。

## Limits
- SSE/NDJSON 协议格式化属宿主层，Core 不负责
- ReasoningContent 不产出给消费者，仅记录在 EngineChatMessage 中
- 取消/失败写回使用 `CancellationToken.None` 确保 partial 消息不丢失

## Source
- Core: `src/Core/Execution/Service.cs`, `Pipeline.cs`
- Contracts: `src/Core/Abstract/IAgentPipeline.cs`
- Tests: `test/OpenAgent.Core.Tests/Conversation/AgentRunExecutionTests.cs`
