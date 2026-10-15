# Permission

身份契约 `IAgentUserContext` 位于 Contracts，HTTP 认证与身份建立由 Engine/Hosting 和宿主管道负责。资源授权接口、门闩与实现位于 Capabilities/Security；Application 在运行配置解析、能力发现和实际调用时接入授权，不在业务能力中引入宿主或 SDK 依赖。

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
**Partial** — 已提供 Claims 授权与可替换的 `IAgentAuthorizationService`，尚无按资源 ID 的 RBAC/ABAC 规则库。`Authorization:Mode` 在能力域注册时选择实现；Options 默认 `AllowAll`，Engine.Host 随仓库提供的 `appsettings.json` 显式配置为 `Claims`。

Claims 模式拒绝未认证身份；`Admin` 角色允许全部资源；其余身份按 `scope`、`scp`、`permissions` 声明中的空格分隔值匹配 `agent.admin`、`{resource}.{action}` 或 `agent.{action}`，角色和权限值不区分大小写。`AllowAll` 实现直接放行，不检查认证状态，不能将它视为认证保护。

## Limits
- Claims 模式按资源类型和动作判断，不检查具体 `ResourceId`；对象归属与租户隔离仍由入口和业务服务单独校验
- 需要资源级策略时通过 DI 替换 `IAgentAuthorizationService`；本次结构迁移不新增 RunGrant 或审批机制
- `ResourceId` 只能包含业务标识，不得包含 API key、token 等敏感信息

## Source
- Interface: `Backend/src/OpenAgent.Capabilities/Security/IAgentAuthorizationService.cs`
- Implementation: `Backend/src/OpenAgent.Capabilities/Security/ClaimsAgentAuthorizationService.cs`、`Backend/src/OpenAgent.Capabilities/Security/AllowAllAgentAuthorizationService.cs`、`Backend/src/OpenAgent.Capabilities/Security/AgentAuthorizationGate.cs`
- Registration: `Backend/src/OpenAgent.Capabilities/Extensions/SecurityServiceExtensions.cs`；由 `AddAgentApplication` 组合注册
- Contract: `Backend/src/OpenAgent.Contracts/Security/AgentUserContext.cs`, `Backend/src/OpenAgent.Contracts/Security/AgentAuthorization.cs`
- Tests: `Backend/tests/OpenAgent.Capabilities.Tests/Security/AgentAuthorizationGateTests.cs`
