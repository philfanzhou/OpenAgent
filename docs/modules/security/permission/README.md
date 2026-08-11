# 统一权限

权限模块包含两层：`IAgentUserContext` 判断用户是否已认证，`IAgentAuthorizationService` 对六类运行时资源进行细粒度授权。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 认证评估 | `IAgentUserContext` 判断认证状态（`IsAuthenticated`） |
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
**Partial** — 认证状态由 `IAgentUserContext` 承载。细粒度决策委托给可替换的 `IAgentAuthorizationService`，仓库内不提供具体 RBAC/ABAC 规则库。默认 `AllowAllAgentAuthorizationService` 保持旧配置兼容。

Router 在调用意图识别 Agent 前先移除无权访问的候选项，因此用户消息、Agent 名称和描述只会与权限内的数据组合。Engine 的 `ClaimsAgentAuthorizationService` 不再读取本地角色策略，只消费票据中的最终 permission；MCP、Skill 和模型调用沿用同一授权结果。

## Source
- Interface: `Backend/src/OpenAgent.Core/Security/IAgentAuthorizationService.cs`
- Implementation: `Backend/src/OpenAgent.Core/Security/AllowAllAgentAuthorizationService.cs`, `Backend/src/OpenAgent.Core/Security/AgentAuthorizationGate.cs`
- Contract: `Backend/src/OpenAgent.Contracts/Security/AgentUserContext.cs`, `Backend/src/OpenAgent.Contracts/Security/AgentAuthorization.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Security/AgentAuthorizationGateTests.cs`
