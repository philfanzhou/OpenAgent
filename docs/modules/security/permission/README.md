# 统一权限

权限模块把认证适配、纯权限决策、委托签发与运行时复核分开。认证失败的请求不会构造 `AuthorizationSubject`。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 纯权限决策 | `OpenAgent.Authorization` 不依赖 HTTP、JWT、ClaimsPrincipal 或具体票据格式 |
| 资源授权 | `IAgentAuthorizationService` 对 Agent/Model/Tool/Function/MCP/Skill 六类资源授权 |
| 双阶段校验 | 发现阶段过滤可见性，执行阶段复核权限 |
| 统一拒绝语义 | `AgentAuthorizationGate` 拒绝时统一返回 `PermissionDenied` |

```text
认证凭据（JWT / mTLS / API Key / SSO）
                    │
                    ▼
            已认证身份上下文
                    │
                    ▼
          AuthorizationSubject
                    │
                    ▼
      IPermissionAuthorizationService
             ├─ 过滤 Agent 目录
             ├─ 过滤意图识别候选集
             ├─ 拒绝未授权显式 Agent
             └─ 裁剪 DelegatedAuthorization
                    │
                    ▼
      IDelegatedAuthorizationIssuer
                    │
          ┌─────────┴─────────┐
          ▼                   ▼
       Engine           第三方 Agent（显式启用）
          │
          └─ Agent / Model / Tool / Function / MCP / Skill 执行检查
```

## Current Status
**Partial** — Router 的 `IPermissionAuthorizationService` 可从 permission claim 与配置角色映射形成决策，Core 的 `IAgentAuthorizationService` 复核 Agent/Model/Tool/Function/MCP/Skill。仓库仍没有 Tenant/Membership 权威模型、完整 RBAC/ABAC 管理面、grant replay store 或生产密钥轮换。

Router 在调用意图识别 Agent 前先移除无权访问的候选项，因此用户消息、Agent 名称和描述只会与权限内的数据组合。Engine 的 `ClaimsAgentAuthorizationService` 不再读取本地角色策略，只消费票据中的最终 permission；MCP、Skill 和模型调用沿用同一授权结果。

## Source
- Interface: `Backend/src/OpenAgent.Core/Security/IAgentAuthorizationService.cs`
- Reusable contracts: `Backend/src/OpenAgent.Authorization/PermissionAuthorization.cs`
- Router adapter and delegation: `Backend/src/OpenAgent.Router/Security/PermissionAuthorizationExtensions.cs`
- Implementation: `Backend/src/OpenAgent.Core/Security/AllowAllAgentAuthorizationService.cs`, `Backend/src/OpenAgent.Core/Security/AgentAuthorizationGate.cs`
- Contract: `Backend/src/OpenAgent.Contracts/Security/AgentUserContext.cs`, `Backend/src/OpenAgent.Contracts/Security/AgentAuthorization.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Security/AgentAuthorizationGateTests.cs`
