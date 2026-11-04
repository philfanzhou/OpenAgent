# Auth

认证只负责建立可验证的请求身份，不负责角色、Agent ACL、能力权限或租户授权决策。

## 当前行为

| 能力 | 行为 |
|---|---|
| 默认登录 | Basic 校验配置的账号密码；可选企业 IdP + OIDC PKCE |
| 可选 JWT API | JWT Bearer 校验 issuer、audience、签名和 lifetime，不接受 URL token；启用第三方 API Key 后，API Key 请求由数据库校验 Bearer 凭据，不访问 Keycloak |
| 密码登录 | `/api/v1/auth/password/token` 校验配置的 Basic 账号；内置账号仅限 Development 且未配置密码 |
| 登录态 | access token、refresh token、PKCE verifier/state 仅存于 `sessionStorage`，不进入 `localStorage`；OIDC 会在 access token 到期前自动续期 |
| 租户 | Production 仅信任 `tenant_id`/`tid` claim；客户端 tenant header 不覆盖 claim，不一致返回 403 |
| Development 兼容 | 仅 Development 的 Basic 认证允许 `X-Tenant-Id` 作为 claim 缺失时的回退，并由 Router 净化后转发 |
| 失败 | 401 清理会话并重新登录；403 保留身份并交由授权界面处理 |
| 授权 | 角色、Agent ACL、能力和租户授权由服务端独立策略处理 |

## 配置

```json
{
  "Authentication": {
    "Mode": "Basic",
    "EnableKeycloak": false,
    "BasicUsername": "admin",
    "BasicPassword": "<部署时注入>",
    "BasicTenantId": "development",
    "Authority": "",
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

## 第三方 API Key 模式

第三方 API Key 是独立于 Basic/JWT Bearer 的新增认证方案，可以与原有方案并存。第三方集成只需开启
`Authentication:EnableApiKey`；API Key
和其绑定的用户、租户、权限全部保存在 PostgreSQL 的 `openagent.third_party_api_keys`
表中；Router 将 Bearer 凭据交给内部 Engine 查询，明文 Key 不进入配置文件或代码库：

```bash
export OPENAGENT_AUTH_ENABLE_API_KEY=true
```

`Authentication:EnableKeycloak` 仍独立控制 Keycloak/OIDC 登录入口，不影响 API Key 请求；关闭它也不会移除已配置的 Basic/JWT Bearer 方案。

请求只支持 `Authorization: Bearer <api-key>`。当前迁移包含两个仅用于本地演示的种子：
`oa_demo_tenant_a_2026` 和 `oa_demo_tenant_b_2026`，分别绑定 `tenant-a` 和 `tenant-b`，
权限为 `agent.execute model.invoke`，且默认 `IsEnabled=false`；本地联调时需在数据库中显式启用。生产部署必须替换种子凭据或保持禁用。

API Key 的启用状态、租户和 scope 由数据库记录决定，调用方不能通过租户 Header 覆盖。
轮换时新增记录、切换调用方凭据，再将旧记录的 `IsEnabled` 设置为 `false`。

API Key 模式仍然执行 Agent ACL、能力权限、租户隔离、限流和审计。生产环境应将 Engine
保持为内网服务；Router 与 Engine 必须使用同一个数据库。

选择 JwtBearer 后缺少 `Authority`、`Audience` 或 `ClientId` 会启动校验失败；启用 HTTPS metadata 时 Authority
必须为 HTTPS。Basic 默认启用，Keycloak 默认关闭；非 Development 环境必须配置 `BasicPassword`。启用 API Key 后，API Key 仍可独立工作，
已配置的 Basic/JWT Bearer 方案仍按原规则处理。Development 可显式启用匿名兼容，
但默认关闭。`EnableApiKey` 默认关闭；开启后使用数据库中的启用记录认证。

`X-Tenant-Id` 只用于 Development 的 Basic 联调。Production 中该 header 不能建立或覆盖租户身份；
缺少租户 claim 的受保护资源请求会被拒绝，header 与 claim 不一致时返回 403。Router 仅在 Development
向 Engine 转发从认证上下文解析并净化后的租户值，Production 转发始终移除租户 header。

`GET /api/v1/auth/config` 可匿名读取公开登录参数；Basic 密码端点在 Basic 模式映射，并与 API Handler 共用账号密码校验；配置密码后禁用开发内置账号。
前端交换 OIDC code 后立即清理回调查询参数；OIDC refresh token 仅用于当前标签页的自动续期，不会进入 `localStorage`、URL、日志或错误详情。退出登录或 refresh token 失效后才要求重新进行交互式登录。

## Source

- Handler: `Backend/src/OpenAgent.Hosting/Security/BasicAuthenticationHandler.cs`
- Third-party handler: `Backend/src/OpenAgent.Hosting/Security/ApiKeyAuthenticationHandler.cs`
- Third-party identity store: `Backend/src/OpenAgent.Infrastructure/Security/EfThirdPartyApiKeyIdentityResolver.cs`
- Registration: `Backend/src/OpenAgent.Hosting/Authentication/AgentAuthenticationExtensions.cs`
- Public config and Development login: `Backend/src/OpenAgent.Hosting/Authentication/AuthenticationEndpointExtensions.cs`
- Context mapping: `Backend/src/OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs`
- Frontend session and PKCE: `Frontend/OpenAgent.Chat/src/api.ts`, `Frontend/OpenAgent.Chat/src/auth.ts`
