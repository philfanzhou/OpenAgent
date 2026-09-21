# Execution — 执行管线与核心调度

核心执行逻辑：执行入口编排、流式推理、错误处理和会话锁。核心代码：执行入口 `Backend/src/OpenAgent.Core/Runtime/Agent/AgentExecutor.cs`；会话锁 `Backend/src/OpenAgent.Core/Conversation/Lock/InMemoryConversationLock.cs`；安全服务 `Backend/src/OpenAgent.Core/Security/`；错误码 `Backend/src/OpenAgent.Contracts/Requests/AgentErrorCode.cs`；异常类 `Backend/src/OpenAgent.Contracts/Security/Exceptions.cs`。

## 执行管线

`AgentExecutor` 是执行入口，编排 `FileAssetRequestResolver → ConversationAgentResolver → IAgentRuntimeResolver → AgentFactory.CreateAsync → AIAgent.Run[Streaming]Async`。横切关注点（认证、租户校验、异常映射）由 Engine.Host 的 ASP.NET Core 中间件承担，AgentExecutor 不实现中间件抽象，也不负责中间件注册顺序（由 DI 容器决定）。

```text
AgentRequest
  -> AgentExceptionHandlerMiddleware
  -> AgentUserContextMiddleware
  -> EngineAdmissionMiddleware
  -> AgentExecutor.Execute[Streaming]Async
```

- 流式和非流式入口分别为 `ExecuteAsync` / `ExecuteStreamingAsync`，共享同一组 resolver 与工厂编排。
- 业务逻辑由 MAF Agent 与各 CapabilitySource 承担，AgentExecutor 仅负责执行编排。
- 异常（流式与非流式）不在 `AgentExecutor` 内捕获，向上传播由 `AgentExceptionHandlerMiddleware` 映射为 HTTP 错误响应（ProblemDetails）；流式路径在 SSE 协议边界映射。`AgentExecutor` 不返回 `Success=false` 形式的错误响应。
- 流式请求正确传递 `CancellationToken`；`UserId, TenantId, Roles, Groups, Claims, Audience, TraceId, ConversationId, AgentId` 正确传递。
- 日志：入口记录 Query、TraceId、UserId；出口记录 Success、ErrorCode；异常不吞没。
- 错误消息格式：AgentException 继承原始异常的 ErrorCode 和 Message；非 AgentException 映射为 ErrorCode=InternalError、Message=ex.Message。

命名约定：执行入口类名固定为 `AgentExecutor`；Engine.Host 中间件类名使用名词（如 `AgentUserContextMiddleware`、`EngineAdmissionMiddleware`、`AgentExceptionHandlerMiddleware`）。

## 流式输出

流式推理输出通过 `IAsyncEnumerable<string>` 持续产出模型内容，支持工具调用中间状态透传、取消信号传播和异常向上报告。

```text
AgentExecutor.ExecuteStreamingAsync
  -> AIAgent.RunStreamingAsync
  -> ChatClientAgent.RunStreamingAsync
  -> AgentResponseUpdate（MAF 原生）
  -> assistant chunks / tool markers / usage
```

- 工具调用中间状态：产出 `ToolCall` 类型 `AgentStreamEvent`，含 `ToolName`/`ToolCallId`/`ToolArguments`；工具调用事件按 `CallId`/`Name` 去重后广播。
- 取消/失败写回 partial 消息并标记状态（使用 `CancellationToken.None` 确保 partial 消息不丢失）。
- 终态用量：Provider usage 只随 `done` SSE 事件发送，缺失时为 `null`；不根据流式文本片段估算 Token。
- ReasoningContent 作为 `Reasoning` 类型 `AgentStreamEvent` 产出给消费者。
- SSE/NDJSON 协议格式化属宿主层，Core 不负责。

源码：`Backend/src/OpenAgent.Core/Runtime/Agent/`；测试 `Backend/tests/OpenAgent.Core.Tests/Runtime/AgentExecutorUsageTests.cs`、`Backend/tests/OpenAgent.Engine.Tests/Hosting/AgentStreamWriterTests.cs`。

## 错误处理

统一错误处理定义执行层异常分类、传播规则和会话写回保障，确保异常不吞没、不丢失上下文。

- 异常分类：`AgentException` 携带 `AgentErrorCode`（10 大类 30+ 错误码）。
- 异常传播：`AgentExecutor` 不捕获异常，向上传播至 Engine.Host 中间件映射。
- 工具异常隔离：MAF 工具执行中 AgentException 直接 throw；其他 Exception 返回 `"Error executing tool: ..."` 错误文本，不中断推理循环。
- 会话写回保障：取消/失败时 `PlatformChatHistory.DisposeAsync`（经 `AgentExecutionScope` 释放触发）以 `CancellationToken.None` 写回 partial 消息，状态置为 Cancelled。
- HTTP 状态码映射属宿主层（见 [engine.md](./engine.md) 的 Host 错误处理）；流式路径异常由 Host 层经 `ExceptionDispatchInfo` 在 SSE 边界映射。

源码：Contracts `Backend/src/OpenAgent.Contracts/Security/Exceptions.cs`、`Requests/AgentErrorCode.cs`；Core `Backend/src/OpenAgent.Core/Runtime/Agent/`。

## 会话锁

`IConversationLock` 按 `{tenantId}:{conversationId}` 串行化请求。配置 Redis 时，`RedisConversationLock` 使用 `SET NX` 获取带 TTL 的分布式锁，并在长推理期间以 Lua 校验所有者续租；锁获取失败时请求返回冲突，释放由 `IConversationLockHandle.DisposeAsync` 完成。未配置 Redis 时，`InMemoryConversationLock` 仅用于单个 Engine 进程的开发/测试降级。

跨实例写入正确性同时由分布式锁和数据库乐观并发保障：EF Core Provider 以 `expectedVersion` 与并发令牌执行检查，冲突时不覆盖已提交的会话消息。

`IConversationLock` 不绑定 Redis；后续可以替换为数据库 advisory lock、Consul 等协调 Provider，而不改变会话存储或业务契约。Redis 仅保存锁令牌和可失效热副本，不是会话或文件资产的持久化存储。
