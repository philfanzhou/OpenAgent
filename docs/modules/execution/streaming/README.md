
## Feature


## 核心用户故事

作为 Agent 系统的消费者，我希望在推理过程中持续接收模型生成的内容片段，以便在长文本生成场景下获得即时反馈，而不必等待完整响应。

## 功能名称和一句话概括

流式推理输出 — 通过 IAsyncEnumerable\<string\> 持续产出模型内容，支持工具调用中间状态透传、取消信号传播和异常向上报告。

## 补充约束

- 流式路径与非流式路径共享同一套配置解析、工具装配和会话语义
- 流式路径经过完整的中间件链
- 流式过程中工具调用以 `[Calling tool: X]` 形式透传给消费者
- 取消和异常时必须写回已产生的 partial 消息
- Core 层不负责 SSE/NDJSON 协议格式化

## 关键验收条件摘要

- [ ] 流式路径持续 yield return 内容片段
- [ ] 工具调用前后输出中间状态
- [ ] 取消信号通过 WithCancellation 正确传播
- [ ] 异常发生时已产生内容写回会话
- [ ] 流式与非流式使用相同的配置和工具装配逻辑

## 明确列出"范围外"

- SSE/NDJSON 响应格式定义（属宿主层）
- 浏览器端重连逻辑
- 前端展示样式

## 文档索引

- [02-SPEC.md](./02-SPEC.md) — 详细需求规格
- [03-DESIGN.md](./03-DESIGN.md) — 设计说明
- [04-TASKS.md](./04-TASKS.md) — 任务清单
- [05-TESTS.md](./05-TESTS.md) — 测试计划
- [06-CONVENTIONS.md](./06-CONVENTIONS.md) — 约定与规范

## Specification


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

## Design


```text
Pipeline.ExecuteStreamAsync
  -> AgentRun.RunStreamingAsync
  -> ChatClientAgent.RunStreamingAsync
  -> AgentResponseUpdate + MafResponseReader
  -> assistant chunks / tool markers / usage
```

函数迭代由 MAF 管理。`PlatformChatHistory` 接收 MAF 的完成/失败通知，写回最终或
partial assistant，并释放会话锁。平台流边界只投影文本、工具提示和 usage。

## Tasks


> 本功能已实现完成，无待办任务。以下为代码评审清单。

```json
[
  {
    "id": "TASK-01",
    "status": "implemented",
    "depends_on": [],
    "action": "ExecuteStreamAsync 基本流式输出（逐 chunk yield return）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "内容片段持续产出"
  },
  {
    "id": "TASK-02",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "流式 ToolCall 合并（MergeToolCalls + EnsureToolCallIds）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "流式 chunk 中的 ToolCall 片段正确合并"
  },
  {
    "id": "TASK-03",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "流式工具调用中间状态输出（[Calling tool: X]）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "工具调用前输出中间状态文本"
  },
  {
    "id": "TASK-04",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "流式取消处理（partial 消息写回 + Cancelled 状态）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "取消时写回 partial 消息，状态为 Cancelled"
  },
  {
    "id": "TASK-05",
    "status": "implemented",
    "depends_on": ["TASK-01"],
    "action": "流式失败处理（partial 消息写回 + Failed 状态 + rethrow）",
    "files": ["src/Core/Execution/Service.cs"],
    "acceptance": "失败时写回 partial 消息，状态为 Failed，异常重新抛出"
  },
  {
    "id": "TASK-06",
    "status": "implemented",
    "depends_on": [],
    "action": "Pipeline 流式路径（中间件链 + ExecuteCoreStreamAsync）",
    "files": ["src/Core/Execution/Pipeline.cs"],
    "acceptance": "流式路径经过完整中间件链"
  },
  {
    "id": "TASK-07",
    "status": "implemented",
    "depends_on": [],
    "action": "中间件 InvokeStreamAsync 接口与 WithCancellation 传播",
    "files": ["src/Core/Abstract/IAgentPipeline.cs"],
    "acceptance": "取消信号通过中间件链正确传播"
  }
]
```

## Tests


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

## Conventions


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
