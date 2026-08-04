# Streaming — 约定与规范 (CONVENTIONS)

## 命名约定

- 流式方法统一使用 `ExecuteStreamAsync` / `InvokeStreamAsync` 命名
- 流式委托类型为 `AgentStreamPipelineDelegate`，返回 `IAsyncEnumerable<string>`
- 流式 chunk 枚举器变量命名为 `chunkEnumerator`

## 接口约定

- 所有流式方法必须使用 `[EnumeratorCancellation]` 标注 CancellationToken 参数
- 流式路径必须使用 `.WithCancellation(cancellationToken)` 传播取消信号
- 中间件的 `InvokeStreamAsync` 必须与 `InvokeAsync` 行为一致（仅交付方式不同）

## 内容产出约定

- 内容片段通过 `yield return` 产出
- 工具调用中间状态格式：`\n[Calling tool: {toolName}]\n`
- ReasoningContent 不产出给消费者，仅记录在 EngineChatMessage 中

## 异常处理约定

- 流式 chunk 枚举异常通过 try/catch 包裹 `MoveNextAsync` 捕获
- 捕获的异常存入 `terminalException`，在流结束后通过 `ExceptionDispatchInfo.Capture` 重新抛出
- 取消和失败均先写回 partial 消息，再抛出异常
- 写回时使用 `CancellationToken.None`，确保 partial 消息不丢失

## ToolCall 合并约定

- 流式 chunk 中的 ToolCall 通过 `MergeToolCalls` 逐步合并
- 相同 Id 的 ToolCall 合并时，ArgumentsJson 逐步拼接
- 合并完成后通过 `EnsureToolCallIds` 确保每个 ToolCall 有唯一 Id（空 Id 生成 GUID）
- Name 为空的 ToolCall 片段被忽略

## 会话写回约定

- 正常完成时统一写回所有消息
- 取消/失败时通过 `PersistPartialAssistantMessage` 写回未记录的 assistant 内容
- MaxTurns 达到时写回最后一条 assistant 消息
