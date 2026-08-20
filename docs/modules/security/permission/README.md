# Permission

权限模块包含两层：`IAgentUserContext` 判断用户是否已认证，`IAgentAuthorizationService` 对六类运行时资源进行细粒度授权。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 认证评估 | `IAgentUserContext` 判断认证状态（`IsAuthenticated`） |
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
**Partial** — 认证状态由 `IAgentUserContext` 承载。细粒度决策委托给可替换的
`IAgentAuthorizationService`：`ClaimsAgentAuthorizationService` 支持 Admin 角色和 action scope，
`AllowAllAgentAuthorizationService` 保持开发兼容。生产默认配置显式选择 Claims，类型默认值和
Development 配置仍是 AllowAll。

## Limits
- AllowAll 不检查认证；Claims 会拒绝匿名，但不检查 `ResourceId`、租户、所有者或 Agent 绑定
- Router 的 Agent 可见性 ACL 与 Core 授权不是同一决策，ACL 读取失败或不存在时当前会放行
- RAG 和 Conversation 尚未进入 `AgentResourceType`，能力发现后也没有统一的调用时复核
- `ResourceId` 只能包含业务标识，不得包含 API key、token 等敏感信息
- 目标资源模型、失败关闭和 Policy Service 边界见
  [`ADR-0004`](../../../decisions/0004-Authentication-Authorization-Trust-Boundary.md)

## Source
- Interface: `Backend/src/OpenAgent.Core/Security/IAgentAuthorizationService.cs`
- Implementation: `Backend/src/OpenAgent.Core/Security/AllowAllAgentAuthorizationService.cs`, `Backend/src/OpenAgent.Core/Security/ClaimsAgentAuthorizationService.cs`, `Backend/src/OpenAgent.Core/Security/AgentAuthorizationGate.cs`
- Contract: `Backend/src/OpenAgent.Contracts/Security/AgentUserContext.cs`, `Backend/src/OpenAgent.Contracts/Security/AgentAuthorization.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Security/AgentAuthorizationGateTests.cs`
