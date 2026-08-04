# Auth

Auth 中间件是 Agent Pipeline 中的安全关卡，负责验证请求用户是否已通过认证。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 认证检查 | 通过 `IPermissionEvaluator.IsAuthenticatedAsync` 判断认证状态 |
| 双路径覆盖 | 同步 `InvokeAsync` 与流式 `InvokeStreamAsync` 共享 `EnsureAuthenticatedAsync` |
| 异常拒绝 | 未认证时抛出 `AgentException(PermissionDenied, "User is not authenticated")` |

## Architecture
```text
请求 → Auth（首个中间件）→ TenantValidation → 后续中间件
        │
   IsAuthenticated=false → 抛 AgentException(PermissionDenied)
   IsAuthenticated=true  → 继续管道
```

## Current Status
**Implemented** — 作为 Pipeline 第一个中间件，依赖注入 `IPermissionEvaluator`，默认实现 `PermissionEvaluator` 仅检查 `IsAuthenticated` 属性。

## Limits
- 默认实现仅检查 `IsAuthenticated` 属性，不处理 M2M、Audience 等复杂场景
- 复杂认证逻辑应通过替换 `IPermissionEvaluator` 实现，不修改中间件本身
- 不使用 HTTP 401/403 语义，由宿主层负责 HTTP 映射

## Source
- Implementation: `Backend/src/OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs`, `Backend/src/OpenAgent.Engine.Host/Middleware/EngineAdmissionMiddleware.cs`（Auth 中间件已移入 Host）
- Contract: `Backend/src/OpenAgent.Core/Security/IAgentAuthorizationService.cs`
- Tests: 无专门测试文件（待补充）
