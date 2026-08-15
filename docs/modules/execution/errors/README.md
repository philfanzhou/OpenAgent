# Execution Error Handling

统一错误处理定义执行层异常分类、传播规则和会话写回保障，确保异常不吞没、不丢失上下文。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 异常分类 | `AgentException` 携带 `AgentErrorCode`（10 大类 30+ 错误码）|
| 异常传播 | `AgentExecutor` 不捕获异常，向上传播至 Engine.Host 中间件映射 |
| 工具异常隔离 | 非 AgentException 返回错误文本，不中断推理循环 |
| 会话写回保障 | 取消/失败时写回 partial 消息（`CancellationToken.None`）|

## Architecture
```text
AgentExecutor 层:
  不捕获异常；AgentException 与通用 Exception 均向上传播

MAF 工具执行:
  AgentException → 直接 throw
  Exception      → return "Error executing tool: ..."

异常映射（Engine.Host 中间件）:
  AgentExceptionHandlerMiddleware 捕获异常 → 映射为 HTTP 错误响应（ProblemDetails）

会话写回取消/失败:
  PlatformChatHistory.DisposeAsync（经 AgentExecutionScope 释放触发）以 CancellationToken.None 写回 partial 消息，状态置为 Cancelled
```

## Current Status
**Implemented** — 异常分类、工具异常处理、写回保障均已落地；异常到 HTTP 的映射由 Engine.Host 的 `AgentExceptionHandlerMiddleware` 承担（`AgentExecutor` 不做转换）。

## Limits
- HTTP 状态码映射属宿主层，Core 不负责
- 流式路径异常由 `AgentExceptionHandlerMiddleware`（Host 层）经 `ExceptionDispatchInfo` 在 SSE 边界映射

## Source
- Contracts: `Backend/src/OpenAgent.Contracts/Security/Exceptions.cs`, `Backend/src/OpenAgent.Contracts/Requests/AgentErrorCode.cs`
- Core: `Backend/src/OpenAgent.Core/Runtime/Agent/`
- Tests: 无专门测试文件（待补充）
