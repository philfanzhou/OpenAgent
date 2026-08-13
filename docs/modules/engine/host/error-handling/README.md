# Host Error Handling

ErrorHandling 模块提供 Agent.Engine 的全局异常处理机制，确保所有未处理异常都被转换为结构化的错误响应。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 全局异常捕获 | `AgentExceptionHandlerMiddleware` 捕获常规 HTTP 异常，返回 ProblemDetails |
| 流式错误载荷 | `StreamingPayloadFactory` 构造 SSE 错误载荷 |
| AgentErrorCode 映射 | 按 ErrorCode 分组映射 HTTP 状态码（403/404/429/400/503/500）|

## Architecture
```text
HTTP 请求
  → AgentExceptionHandlerMiddleware（SSE 异常→error/done；其他异常→ProblemDetails）
  → Endpoint Handlers
```

## Exception Mapping
| Exception | HTTP |
|-----------|------|
| UnauthorizedAccessException | 403 |
| HumanApprovalRequiredException | 202 |
| AgentException | 按 ErrorCode |
| TimeoutException | 504 |
| 其他 | 500 |

## Current Status
**Partial** — 功能已实现，但缺少测试覆盖（规划中）。

## Limits
- 响应已开始时重新抛出异常，不写入 ProblemDetails

## Source
- Core: `Backend/src/OpenAgent.Engine.Host/Middleware/AgentExceptionHandlerMiddleware.cs`
- Payload: `Backend/src/OpenAgent.Engine.Host/StreamingPayloadFactory.cs`
- Orchestration: `Backend/src/OpenAgent.Engine.Host/Program.cs`
- Tests: `Backend/tests/OpenAgent.Engine.Tests/Hosting/AgentExceptionHandlerMiddlewareTests.cs`
