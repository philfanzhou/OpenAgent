# 统一权限

权限策略只在 Gateway 计算一次，下游使用同一份短时签名授权票据执行强制校验，避免 Router、Engine 和 MCP 各自维护不一致的 RBAC 规则。

## 决策与执行

```text
JWT claims + authenticated defaults + role grants
                    │
                    ▼
          Router permission evaluator
             ├─ 过滤 Agent 目录
             ├─ 过滤意图识别候选集
             ├─ 拒绝未授权显式 Agent
             └─ 签发短时授权票据
                    │
          ┌─────────┴─────────┐
          ▼                   ▼
       Engine           第三方 Agent（显式启用）
          │
          └─ Agent / Model / Tool / Function / MCP / Skill 执行检查
```

权限支持通用授权和资源约束两种形式：`agent.execute` 允许执行任意 Agent，`agent.execute:finance` 只允许指定 Agent，`agent.execute:*` 是显式通配。管理、会话和能力测试分别使用 `agent.config.*`、`conversation.*` 与 `capability.test`。

Router 在调用意图识别 Agent 前先移除无权访问的候选项，因此用户消息、Agent 名称和描述只会与权限内的数据组合。Engine 的 `ClaimsAgentAuthorizationService` 不再读取本地角色策略，只消费票据中的最终 permission；MCP、Skill 和模型调用沿用同一授权结果。

第三方 Agent 默认只收到其服务凭据，不收到用户身份或内部票据。只有同时配置 `ForwardGatewayGrant=true` 和独立 `GatewayAudience` 时，Router 才签发 audience 绑定的票据；第三方必须使用相同协议验证签名、有效期和 audience。

## 默认生产策略

普通已认证用户拥有 Agent 读取/执行、模型与能力使用、本人会话读删和身份读取权限。`OpenAgent.Admin` 角色映射到 `*`，用于配置管理和连接测试。部署方可用 JWT scope 或 `GatewayAuthorization:RolePermissions` 收紧策略。

密钥轮换、多 issuer 并行验证和集中策略服务不在当前仓库实现范围；这些能力可通过替换 `IGatewayAuthorizationService` 接入，而不改变 Router、Engine 或第三方适配器。
