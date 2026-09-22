# Execution Error Handling

统一错误处理定义执行层异常分类、传播规则和会话写回保障，确保异常不吞没、不丢失上下文。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 异常分类 | `AgentException` 携带 `AgentErrorCode`（10 大类 30+ 错误码）|
| 异常传播 | `AgentExecutor` 不捕获异常，向上传播至 Engine.Host 中间件映射 |
| 工具异常隔离 | 工具返回结构化 `ToolResult`；未捕获异常脱敏后以统一错误信封回传模型，不中断推理循环 |
| 统一错误信封 | `{"error":"...","code":"...","hint":"...",("timedOut":true)添加于超时,("errorId":"...")添加于脱敏异常}`；code 稳定供程序判别，hint 给模型可行动修正路径 |
| 工具调用超时 | 单次调用超过 `AgentExecution:ToolCallTimeoutSeconds`（默认 300，≤0 不限时）被主动取消，以带 `timedOut` 标记的错误结果回传模型，会话不中断 |
| 结果预算截断 | 工具结果超过分级字符预算（读 80k/执行 40k/MCP 48k/默认 60k，`AgentExecution:*ToolResultCharBudget`，≤0 不限）做头尾保留截断并附收窄提示 |
| 会话写回保障 | 取消/失败时写回 partial 消息（`CancellationToken.None`）|

## Architecture
```text
AgentExecutor 层:
  不捕获异常；AgentException 与通用 Exception 均向上传播

能力层（CapabilityDefinition.Invoke → Task<ToolResult>）:
  校验/业务错误 → ToolResult.Error(message, code, hint)，Content 即错误信封

MAF 工具执行（IsolatedToolFunction 包装所有能力+MCP 工具，模型侧唯一出口）:
  ToolResult(IsError) → 原样回传信封（不截断）
  ToolResult/字符串   → 过分级预算，超限做头尾保留截断 + 收窄提示
  Exception          → 脱敏信封 {"error":"Tool '...' failed with an unexpected error.","code":"tool_error","errorId":"..."}；
                       原始异常只进日志（errorId 关联），不透传给模型
  调用超时/传输层取消（外层未取消）→ {"error":"Tool '...' timed out ...","code":"tool_timeout","timedOut":true}
  运行级取消（用户中止/停机）→ 照常 rethrow OperationCanceledException

异常映射（Engine.Host 中间件）:
  AgentExceptionHandlerMiddleware 捕获异常 → 映射为 HTTP 错误响应（ProblemDetails）

会话写回取消/失败:
  PlatformChatHistory.DisposeAsync（经 AgentExecutionScope 释放触发）以 CancellationToken.None 写回 partial 消息，状态置为 Cancelled
```

## Current Status
**Implemented** — 异常分类、工具异常处理、统一错误信封、结果预算截断、写回保障均已落地；异常到 HTTP 的映射由 Engine.Host 的 `AgentExceptionHandlerMiddleware` 承担（`AgentExecutor` 不做转换）。

## Limits
- HTTP 状态码映射属宿主层，Core 不负责
- 流式路径异常由 `AgentExceptionHandlerMiddleware`（Host 层）经 `ExceptionDispatchInfo` 在 SSE 边界映射
- 预算按字符计（≈4 字符/token），非精确 token 计数

## Source
- Contracts: `Backend/src/OpenAgent.Contracts/Security/Exceptions.cs`, `Backend/src/OpenAgent.Contracts/Requests/AgentErrorCode.cs`, `Backend/src/OpenAgent.Contracts/Capabilities/ToolResult.cs`
- Core: `Backend/src/OpenAgent.Core/Runtime/Agent/`（`IsolatedToolFunction`、`ToolResultBudgets`、`AgentExecutionOptions`）
- Tests: `Backend/tests/OpenAgent.Core.Tests/Runtime/ToolFailureIsolationTests.cs`（工具异常隔离与超时不中断会话）、`Backend/tests/OpenAgent.Core.Tests/Runtime/ToolResultBudgetTests.cs`（预算截断与分级）
