# Permission

权限模块包含两层：`IPermissionEvaluator` 判断用户是否已认证，`IAgentAuthorizationService` 对六类运行时资源进行细粒度授权。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 认证评估 | `IPermissionEvaluator.IsAuthenticatedAsync` 判断认证状态 |
| 资源授权 | `IAgentAuthorizationService` 对 Agent/Model/Tool/Function/MCP/Skill 六类资源授权 |
| 双阶段校验 | 发现阶段过滤可见性，执行阶段复核权限 |
| 统一拒绝语义 | `AgentAuthorizationGate` 拒绝时统一返回 `PermissionDenied` |

## Architecture
```text
IAgentUserContext + AgentAuthorizationRequest
    → IAgentAuthorizationService
    → AgentAuthorizationGate
    → allow 或 AgentException(PermissionDenied)
```

## Current Status
**Partial** — `PermissionEvaluator` 仍只处理认证。细粒度决策委托给可替换的 `IAgentAuthorizationService`，仓库内不提供具体 RBAC/ABAC 规则库。默认 `AllowAllAgentAuthorizationService` 保持旧配置兼容。

## Limits
- 默认实现仅检查 `IsAuthenticated`，不处理角色/声明/租户策略
- 生产部署应通过 DI 替换 `IAgentAuthorizationService` 实现
- `ResourceId` 只能包含业务标识，不得包含 API key、token 等敏感信息

## Source
- Contracts: `Backend/src/OpenAgent.Core/Security/IAgentAuthorizationService.cs`
- Implementation: `Backend/src/OpenAgent.Core/Security/AllowAllAgentAuthorizationService.cs`, `Backend/src/OpenAgent.Core/Security/AgentAuthorizationGate.cs`
- Data: `Backend/src/OpenAgent.Contracts/Security/AgentAuthorization.cs`, `Backend/src/OpenAgent.Contracts/Security/AgentUserContext.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Security/AgentAuthorizationGateTests.cs`
