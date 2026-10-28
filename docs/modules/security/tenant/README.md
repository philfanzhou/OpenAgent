# TenantValidation

TenantValidation 中间件确保请求的用户上下文中包含有效的 TenantId，是多租户数据隔离的第一道防线。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 租户校验 | 检查 `userContext.TenantId` 是否为 null 或空 |
| 入口校验 | `EngineAdmissionMiddleware` 在请求入口校验 TenantId（ASP.NET Core RequestDelegate，仅 `InvokeAsync`） |
| 异常拒绝 | TenantId 缺失时抛出 `TenantDataIsolationException`（ErrorCode 5003） |

## Architecture
```text
请求 → Auth 中间件 → TenantValidation → 后续中间件
                         │
                   TenantId 为空 → 抛 TenantDataIsolationException
                   TenantId 非空 → 继续管道
```

## Current Status
**IMPLEMENTED** — `EngineAdmissionMiddleware` 在请求入口校验 TenantId，缺失时抛 `TenantDataIsolationException`，由 `AgentExceptionHandlerMiddleware` 经 `ErrorMapper` 映射为 HTTP 400。

## Limits
- 仅校验 TenantId 是否存在，不校验租户存在性、状态或数据归属
- 租户存在性/状态检查需扩展此中间件或新增中间件
- 不混入业务逻辑，存储层负责实际数据隔离

## Source
- Implementation: `Backend/src/OpenAgent.Engine.Host/Middleware/EngineAdmissionMiddleware.cs`
- Exception: `Backend/src/OpenAgent.Contracts/Security/Exceptions.cs`
- Tests: 无专门测试文件
