# Host Error Handling

ErrorHandling 模块提供全平台共享的全局异常处理机制，确保所有未处理异常都被转换为结构化的错误响应。核心设施位于 `OpenAgent.Hosting/Errors`，Engine/Router 共用；Engine 通过选项注入自己的 SDK 异常映射与中文 SSE 文案。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 全局异常捕获 | `AgentExceptionHandlerMiddleware`（Hosting 共享）捕获常规 HTTP 异常，返回统一 ProblemDetails |
| 流式错误载荷 | Engine 的 `StreamingPayloadFactory` 构造中文 SSE 错误载荷；默认载荷从 ProblemDetails 投影 |
| AgentErrorCode 映射 | 按 ErrorCode 分组映射 HTTP 状态码（401/403/404/409/429/400/503/500）|
| 统一错误契约 | 扩展字段 `traceId`/`timestamp`/`errorCode`/`code`，见 `docs/overview/API.md` |

## Architecture
```text
HTTP 请求
  → AgentExceptionHandlerMiddleware（Hosting 共享；SSE 异常→error/done；其他异常→ProblemDetails）
    ├─ AgentExceptionMapper（核心映射 + Engine 注册的 ClientResultException 扩展映射）
    └─ AgentProblemDetails（统一载荷构造）
  → Endpoint Handlers
```

## Exception Mapping
| Exception | HTTP |
|-----------|------|
| UnauthorizedAccessException | 403 |
| HumanApprovalRequiredException | 202（含 approvalToken/actionDescription 扩展）|
| AgentException | 按 ErrorCode（`AgentExceptionMapper.MapAgentErrorCode`）|
| TimeoutException | 504 |
| ClientResultException（Engine 扩展映射） | 4xx 或 502（Provider 故障）|
| HttpRequestException | 401/403/404/429/503（Provider HTTP 故障）|
| 其他 | 500 |

## Current Status
**Implemented** — 异常捕获、ProblemDetails/SSE 错误载荷与 ErrorCode→HTTP 映射均已落地，含中间件测试（`OpenAgent.Hosting.Tests`）。

## Limits
- 响应已开始时重新抛出异常，不写入 ProblemDetails（仅流式端点发 error/done 事件）

## Source
- 共享设施：`Backend/src/OpenAgent.Hosting/Errors/`（中间件、映射器、ProblemDetails 构造器、Writer）
- Engine 接入：`Backend/src/OpenAgent.Engine.Host/EngineErrorHandling.cs`
- SSE 载荷：`Backend/src/OpenAgent.Engine.Host/StreamingPayloadFactory.cs`
- Orchestration: `Backend/src/OpenAgent.Engine.Host/Program.cs`
- Tests: `Backend/tests/OpenAgent.Hosting.Tests/AgentExceptionHandlerMiddlewareTests.cs`
- API 规范：`docs/overview/API.md`
