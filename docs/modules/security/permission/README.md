# 统一权限

权限是独立的跨层能力，不是 Gateway 的附属功能。`OpenAgent.Authorization` 是不依赖 ASP.NET、Hosting、Router、Engine 或 Contracts 的纯 .NET 程序集，定义 `PermissionSubject`、权限判定与委托授权票据签发接口，以及权限目录和匹配规则。Router、Engine、MCP/Skill 适配器和第三方服务都可以只引用这一程序集并提供自己的实现。

当前 `OpenAgent.Hosting` 提供的 HMAC 短时票据实现只是一个可替换适配器：它把 HTTP/JWT Claims 映射为 `PermissionSubject`，并注册为 `IPermissionAuthorizer` 与 `IDelegatedPermissionGrantIssuer`。未来接入数据库策略、OPA、企业 IAM 或远程授权服务时，替换这两个接口的实现即可；业务层不引用 Hosting 的网关实现。

## 决策与执行

```text
JWT claims + authenticated defaults + role grants
                    │
                    ▼
        IPermissionAuthorizer
             ├─ 过滤 Agent 目录
             ├─ 过滤意图识别候选集
             ├─ 拒绝未授权显式 Agent
             └─ IDelegatedPermissionGrantIssuer
                    │
          ┌─────────┴─────────┐
          ▼                   ▼
       Engine           第三方 Agent（显式启用）
          │
          └─ Agent / Model / Tool / Function / MCP / Skill 执行检查
```

权限支持通用授权和资源约束两种形式：`agent.execute` 允许执行任意 Agent，`agent.execute:finance` 只允许指定 Agent，`agent.execute:*` 是显式通配。管理、会话和能力测试分别使用 `agent.config.*`、`conversation.*` 与 `capability.test`。

Router 在调用意图识别 Agent 前先移除无权访问的候选项，因此用户消息、Agent 名称和描述只会与权限内的数据组合。Engine 的 `ClaimsAgentAuthorizationService` 不再读取本地角色策略，只消费票据中的最终 permission；MCP、Skill 和模型调用沿用同一授权结果。

第三方 Agent 默认只收到其服务凭据，不收到用户身份或内部票据。只有同时配置 `ForwardGatewayGrant=true`、独立 `GatewayAudience` 和该 audience 的独立签名密钥时，Router 才通过 `IDelegatedPermissionGrantIssuer` 签发 audience 绑定的票据；第三方必须使用自己的密钥验证签名、有效期和 audience，不能获得或复用 Engine 密钥。第三方也可以实现同一接口，选择 JWT、PASETO 或其企业授权服务，而不需要依赖 OpenAgent.Hosting。

## 默认生产策略

普通已认证用户拥有 Agent 读取/执行、模型与能力使用、本人会话读删和身份读取权限。`OpenAgent.Admin` 角色映射到 `*`，用于配置管理和连接测试。部署方可用 JWT scope 或 `GatewayAuthorization:RolePermissions` 收紧策略。

密钥轮换、多 issuer 并行验证和集中策略服务不在当前 Hosting 适配器的实现范围；这些能力可通过替换 `IPermissionAuthorizer` 和/或 `IDelegatedPermissionGrantIssuer` 接入，而不改变 Router、Engine 或第三方适配器。
