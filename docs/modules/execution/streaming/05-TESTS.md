# Streaming — 测试计划 (TESTS)

测试工具：xUnit + Moq
现有测试文件：test/OpenAgent.Core.Tests/Conversation/AgentRunExecutionTests.cs

## 单元测试

### UT-01 流式取消写回

- **Given**：流式执行过程中引擎抛出 OperationCanceledException
- **When**：ExecuteStreamAsync()
- **Then**：写回 user + partial assistant 消息，状态为 Cancelled

### UT-02 流式失败写回

- **Given**：流式执行过程中引擎抛出 InvalidOperationException
- **When**：ExecuteStreamAsync()
- **Then**：写回 user + partial assistant 消息，状态为 Failed

### UT-03 流式工具调用流程

- **Given**：引擎返回包含 ToolCalls 的流式结果
- **When**：ExecuteStreamAsync()
- **Then**：输出 "thinking" → "[Calling tool: lookup_data]" → "final-answer"，消息正确写回

## 遗漏的测试场景

- 流式路径经过中间件链的端到端验证
- 流式 ToolCall 片段逐步合并（MergeToolCalls）的单元测试
- EnsureToolCallIds 为空 Id 生成 GUID 的验证
- 流式 XML \<tool_use\> 工具调用检测
- 流式路径 MaxTurns 达到时的行为
- 流式路径 ReasoningContent 的处理
- 多轮工具调用的流式输出顺序验证
- 取消信号通过 WithCancellation 传播到引擎的验证
- 异常通过 ExceptionDispatchInfo 保留原始堆栈的验证
- 流式路径空工具列表的执行
