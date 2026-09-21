# Security — 安全与租户

Agent 请求的安全关卡：认证、租户隔离及权限评估。

## 认证

认证只负责建立可验证的请求身份，不负责角色、Agent ACL、能力权限或租户授权决策。

| 能力 | 行为 |
|---|---|
| Production 登录 | 前端通过企业 IdP 执行 OIDC Authorization Code + PKCE |
| Production API | JWT Bearer 校验 issuer、audience、签名和 lifetime，不接受 URL token；启用第三方 API Key 后，API Key 请求由数据库校验 Bearer 凭据，不访问 Keycloak |
| Development 登录 | `/api/v1/auth/password/token` 返回 Basic 凭据，仅用于联调且不校验真实密码 |
| 登录态 | access token、refresh token、PKCE verifier/state 仅存于 `sessionStorage`，不进入 `localStorage`；OIDC 会在 access token 到期前自动续期 |
| 租户 | Production 仅信任 `tenant_id`/`tid` claim；客户端 tenant header 不覆盖 claim，不一致返回 403 |
| Development 兼容 | 仅 Development 的 Basic 认证允许 `X-Tenant-Id` 作为 claim 缺失时的回退，并由 Router 净化后转发 |
| 失败 | 401 清理会话并重新登录；403 保留身份并交由授权界面处理 |
| 授权 | 角色、Agent ACL、能力和租户授权由服务端独立策略处理 |

配置：

```json
{
  "Authentication": {
    "Mode": "JwtBearer",
    "Authority": "https://idp.example.com",
    "Audience": "openagent-api",
    "ClientId": "openagent-chat",
    "Scopes": [ "openid", "profile" ],
    "RequireHttpsMetadata": true,
    "ClockSkewSeconds": 60,
    "AllowDevelopmentAnonymous": false,
    "DevelopmentTenantId": "development"
  }
}
```

第三方 API Key 模式：独立于 Basic/JWT Bearer 的新增认证方案，可以与原有方案并存。第三方集成只需开启 `Authentication:EnableApiKey`；API Key 和其绑定的用户、租户、权限全部保存在 PostgreSQL 的 `openagent.third_party_api_keys` 表中；Router 将 Bearer 凭据交给内部 Engine 查询，明文 Key 不进入配置文件或代码库。`Authentication:EnableKeycloak` 仍独立控制 Keycloak/OIDC 登录入口，不影响 API Key 请求。

请求只支持 `Authorization: Bearer <api-key>`。本地演示种子 `oa_demo_tenant_a_2026` 和 `oa_demo_tenant_b_2026` 分别绑定 `tenant-a` 和 `tenant-b`，权限为 `agent.execute model.invoke`，且默认 `IsEnabled=false`；本地联调需在数据库中显式启用，生产部署必须替换种子凭据或保持禁用。API Key 的启用状态、租户和 scope 由数据库记录决定，调用方不能通过租户 Header 覆盖；轮换时新增记录、切换调用方凭据，再将旧记录的 `IsEnabled` 设为 `false`。API Key 模式仍然执行 Agent ACL、能力权限、租户隔离、限流和审计。生产环境应将 Engine 保持为内网服务；Router 与 Engine 必须使用同一个数据库。

启动校验：生产环境缺少 `Authority`、`Audience` 或 `ClientId` 会启动校验失败；启用 HTTPS metadata 时 Authority 必须为 HTTPS；`Basic` 模式在非 Development 环境启动失败；`EnableApiKey` 默认关闭，开启后使用数据库中的启用记录认证；Development 可显式启用匿名兼容，但默认关闭。

`X-Tenant-Id` 只用于 Development 的 Basic 联调。Production 中该 header 不能建立或覆盖租户身份；缺少租户 claim 的受保护资源请求会被拒绝，header 与 claim 不一致时返回 403。Router 仅在 Development 向 Engine 转发从认证上下文解析并净化后的租户值，Production 转发始终移除租户 header。

`GET /api/v1/auth/config` 可匿名读取公开登录参数；Basic 密码端点只在 Development + Basic 模式映射。前端交换 OIDC code 后立即清理回调查询参数；OIDC refresh token 仅用于当前标签页的自动续期，不进入 `localStorage`、URL、日志或错误详情。

源码：Handler `Backend/src/OpenAgent.Hosting/Security/BasicAuthenticationHandler.cs`；第三方 handler `ApiKeyAuthenticationHandler.cs`；第三方身份存储 `Backend/src/OpenAgent.Infrastructure/Security/EfThirdPartyApiKeyIdentityResolver.cs`；注册 `Backend/src/OpenAgent.Hosting/Authentication/AgentAuthenticationExtensions.cs`；公开配置与 Development 登录 `AuthenticationEndpointExtensions.cs`；上下文映射 `Backend/src/OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs`；前端会话与 PKCE `Frontend/OpenAgent.Chat/src/api.ts`、`src/auth.ts`。本地 Keycloak 部署见 [keycloak](../integrations/keycloak.md)。

## 租户校验

TenantValidation 确保请求的用户上下文中包含有效的 TenantId，是多租户数据隔离的第一道防线。

- `EngineAdmissionMiddleware` 在请求入口校验 `userContext.TenantId` 是否为 null 或空（ASP.NET Core RequestDelegate，仅 `InvokeAsync`）。
- TenantId 缺失时抛出 `TenantDataIsolationException`（ErrorCode 5003），由 `AgentExceptionHandlerMiddleware` 经 `ErrorMapper` 映射为 HTTP 400。
- 仅校验 TenantId 是否存在，不校验租户存在性、状态或数据归属（如需检查需扩展中间件）；不混入业务逻辑，存储层负责实际数据隔离。

源码：`Backend/src/OpenAgent.Engine.Host/Middleware/EngineAdmissionMiddleware.cs`；异常 `Backend/src/OpenAgent.Contracts/Security/Exceptions.cs`。

## 权限

权限模块包含两层：`IAgentUserContext` 判断用户是否已认证，`IAgentAuthorizationService` 对六类运行时资源（Agent/Model/Tool/Function/MCP/Skill）进行细粒度授权。

```text
IAgentUserContext + AgentAuthorizationRequest
    -> IAgentAuthorizationService
    -> AgentAuthorizationGate
    -> allow 或 AgentException(PermissionDenied)
```

- 双阶段校验：发现阶段过滤可见性，执行阶段复核权限。
- 统一拒绝语义：`AgentAuthorizationGate` 拒绝时统一返回 `PermissionDenied`。
- 细粒度决策委托给可替换的 `IAgentAuthorizationService`；默认 `AllowAllAgentAuthorizationService` 仅检查 `IsAuthenticated`，保持旧配置兼容，不处理角色/声明/租户策略。
- 生产部署应通过 DI 替换 `IAgentAuthorizationService` 实现；仓库内不提供具体 RBAC/ABAC 规则库。
- `ResourceId` 只能包含业务标识，不得包含 API key、token 等敏感信息。

源码：接口 `Backend/src/OpenAgent.Core/Security/IAgentAuthorizationService.cs`；实现 `AllowAllAgentAuthorizationService.cs`、`AgentAuthorizationGate.cs`；契约 `Backend/src/OpenAgent.Contracts/Security/AgentUserContext.cs`、`AgentAuthorization.cs`；测试 `Backend/tests/OpenAgent.Core.Tests/Security/AgentAuthorizationGateTests.cs`。
