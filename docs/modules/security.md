# Security — 认证、授权与租户

安全域负责 Agent 请求的三道关卡：认证建立可验证的请求身份，权限评估决定资源可用性，租户校验保障多租户数据隔离的第一道防线。

## 认证（Auth）

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

生产环境缺少 `Authority`、`Audience` 或 `ClientId` 会启动校验失败；启用 HTTPS metadata 时 Authority 必须为 HTTPS。`Basic` 模式在非 Development 环境同样启动失败。Development 可显式启用匿名兼容，但默认关闭。

### 第三方 API Key 模式

第三方 API Key 是独立于 Basic/JWT Bearer 的新增认证方案，可以与原有方案并存。第三方集成只需开启 `Authentication:EnableApiKey`（默认关闭）；API Key 和其绑定的用户、租户、权限全部保存在 PostgreSQL 的 `openagent.third_party_api_keys` 表中；Router 将 Bearer 凭据交给内部 Engine 查询，明文 Key 不进入配置文件或代码库。`Authentication:EnableKeycloak` 仍独立控制 Keycloak/OIDC 登录入口，不影响 API Key 请求。

请求只支持 `Authorization: Bearer <api-key>`。当前迁移包含两个仅用于本地演示的种子：`oa_demo_tenant_a_2026` 和 `oa_demo_tenant_b_2026`，分别绑定 `tenant-a` 和 `tenant-b`，权限为 `agent.execute model.invoke`，且默认 `IsEnabled=false`；本地联调时需在数据库中显式启用。生产部署必须替换种子凭据或保持禁用。

API Key 的启用状态、租户和 scope 由数据库记录决定，调用方不能通过租户 Header 覆盖。轮换时新增记录、切换调用方凭据，再将旧记录的 `IsEnabled` 设置为 `false`。API Key 模式仍然执行 Agent ACL、能力权限、租户隔离、限流和审计。生产环境应将 Engine 保持为内网服务；Router 与 Engine 必须使用同一个数据库。

### 端点与前端会话

`GET /api/v1/auth/config` 可匿名读取公开登录参数；Basic 密码端点只在 Development + Basic 模式映射。前端交换 OIDC code 后立即清理回调查询参数；OIDC refresh token 仅用于当前标签页的自动续期，不会进入 `localStorage`、URL、日志或错误详情。退出登录或 refresh token 失效后才要求重新进行交互式登录。

`X-Tenant-Id` 只用于 Development 的 Basic 联调。Production 中该 header 不能建立或覆盖租户身份；缺少租户 claim 的受保护资源请求会被拒绝，header 与 claim 不一致时返回 403。Router 仅在 Development 向 Engine 转发从认证上下文解析并净化后的租户值，Production 转发始终移除租户 header。

## 权限评估（Permission）

身份契约 `IAgentUserContext` 位于 Contracts，HTTP 认证与身份建立由 Engine/Hosting 和宿主管道负责。资源授权接口、门闩与实现位于 Capabilities/Security；Application 在运行配置解析、能力发现和实际调用时接入授权，不在业务能力中引入宿主或 SDK 依赖。

```text
IAgentUserContext + AgentAuthorizationRequest
    → IAgentAuthorizationService
    → AgentAuthorizationGate
    → allow 或 AgentException(PermissionDenied)
```

- 双阶段校验：发现阶段过滤可见性，执行阶段复核权限，避免发现与调用之间权限变化仍沿用旧授权。
- `AgentAuthorizationGate` 拒绝时统一返回 `PermissionDenied`。
- `Authorization:Mode` 在能力域注册时选择实现；Options 默认 `AllowAll`，Engine.Host 随仓库提供的 `appsettings.json` 显式配置为 `Claims`。
- Claims 模式拒绝未认证身份；`Admin` 角色允许全部资源；其余身份按 `scope`、`scp`、`permissions` 声明中的空格分隔值匹配 `agent.admin`、`{resource}.{action}` 或 `agent.{action}`，角色和权限值不区分大小写。`AllowAll` 直接放行，不检查认证状态，不能视为认证保护。

现状：已提供 Claims 授权与可替换的 `IAgentAuthorizationService`，尚无按资源 ID 的 RBAC/ABAC 规则库；Claims 模式按资源类型和动作判断，不检查具体 `ResourceId`，对象归属与租户隔离仍由入口和业务服务单独校验。需要资源级策略时通过 DI 替换 `IAgentAuthorizationService`；不新增 RunGrant 或审批机制。`ResourceId` 只能包含业务标识，不得包含 API key、token 等敏感信息。

## 租户校验（Tenant）

TenantValidation 确保请求的用户上下文中包含有效的 TenantId：`EngineAdmissionMiddleware` 在请求入口（认证中间件之后）校验 `userContext.TenantId`，缺失时抛 `TenantDataIsolationException`（ErrorCode 5003），由 `AgentExceptionHandlerMiddleware` 经 `ErrorMapper` 映射为 HTTP 400。

仅校验 TenantId 是否存在，不校验租户存在性、状态或数据归属；存储层负责实际数据隔离。

## 代码入口

- 认证 Handler 与注册：`Backend/src/OpenAgent.Engine/Hosting/Security/`（BasicAuthenticationHandler、ApiKeyAuthenticationHandler）、`Hosting/Authentication/AgentAuthenticationExtensions.cs`、`AuthenticationEndpointExtensions.cs`。
- 第三方 Key 身份查询：`Backend/src/OpenAgent.Framework/Security/EfThirdPartyApiKeyIdentityResolver.cs`。
- 身份上下文映射：`Backend/src/OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs`；租户校验：`EngineAdmissionMiddleware.cs`。
- 授权接口与实现：`Backend/src/OpenAgent.Contracts/Security/`（AgentUserContext、AgentAuthorization）、`Backend/src/OpenAgent.Capabilities/Security/`（IAgentAuthorizationService、Claims/AllowAll 实现、AgentAuthorizationGate）。
- 测试：`Backend/tests/OpenAgent.Capabilities.Tests/Security/AgentAuthorizationGateTests.cs`。
- 前端会话与 PKCE：`Frontend/OpenAgent.Chat/src/api.ts`、`auth.ts`。
