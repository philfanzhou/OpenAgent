# Streaming — 详细需求规格 (SPEC)

## 功能概述和用户故事

作为 Agent 系统的消费者，我希望在推理过程中持续接收模型生成的内容片段，以便在长文本生成场景下获得即时反馈，而不必等待完整响应。

## 功能要求清单

### 基本流式语义

- [ ] FR-01: 流式路径通过 IAsyncEnumerable\<string\> 产出内容片段
- [ ] FR-02: 流式路径与非流式路径使用同一套配置解析和工具装配逻辑
- [ ] FR-03: 流式路径与非流式路径共享同一套会话语义（历史加载、消息写回）

### 工具调用中间状态

- [ ] FR-04: 工具调用前输出 `\n[Calling tool: {toolName}]\n` 中间状态
- [ ] FR-05: 工具调用结果回填到消息列表，继续后续推理
- [ ] FR-06: 工具调用失败时标记 terminalException，终止流并写回 partial 消息

### 流式 ToolCall 合并

- [ ] FR-07: 流式 chunk 中的 ToolCall 片段通过 MergeToolCalls 逐步合并
- [ ] FR-08: 合并完成后通过 EnsureToolCallIds 确保每个 ToolCall 有唯一 Id
- [ ] FR-09: ArgumentsJson 在流式过程中逐步拼接

### 取消与异常

- [ ] FR-10: CancellationToken 通过 WithCancellation 和 [EnumeratorCancellation] 传播
- [ ] FR-11: 取消时写回已产生的 partial assistant 消息，状态为 Cancelled
- [ ] FR-12: 异常时写回已产生的 partial assistant 消息，状态为 Failed
- [ ] FR-13: 异常通过 ExceptionDispatchInfo.Capture 重新抛出，保留原始堆栈

### 会话写回

- [ ] FR-14: 正常完成时统一写回所有 user/assistant/tool 消息
- [ ] FR-15: 取消/失败时通过 PersistPartialAssistantMessage 写回未记录的 assistant 内容
- [ ] FR-16: MaxTurns 达到时写回最后一条 assistant 消息

## 详细的验收标准

### AC-FR-01: 内容片段产出
- Given: 引擎返回流式 chunk 包含 Content
- When: ExecuteStreamAsync()
- Then: 每个 chunk 的 Content 被 yield return

### AC-FR-04: 工具调用中间状态
- Given: 引擎返回包含 ToolCalls 的流式结果
- When: 工具即将执行
- Then: 消费者收到 `\n[Calling tool: {toolName}]\n` 文本

### AC-FR-11: 取消时写回
- Given: 流式执行过程中收到取消信号
- When: 执行被取消
- Then: 写回 user + partial assistant 消息，状态为 Cancelled，异常向上抛出

### AC-FR-07: ToolCall 片段合并
- Given: 流式 chunk 逐步返回 ToolCall 的 Name 和 ArgumentsJson 片段
- When: MergeToolCalls 处理
- Then: 相同 Id 的 ToolCall 被合并，ArgumentsJson 逐步拼接

## 非功能需求

- 流式输出不应因包装而改变主执行结果的语义
- 取消信号必须及时传播，不应被吞没
- 异常必须保留原始堆栈信息

## 测试策略

- 单元测试覆盖：内容产出、工具调用中间状态、取消写回、失败写回、ToolCall 合并
- 测试文件：`test/OpenAgent.Core.Tests/Conversation/AgentRunExecutionTests.cs`
