# Auth

认证只负责建立可验证的请求身份，不负责角色、Agent ACL、能力权限或租户授权决策。

## 当前行为

| 能力 | 行为 |
|---|---|
| Production 登录 | 前端通过企业 IdP 执行 OIDC Authorization Code + PKCE |
| Production API | JWT Bearer 校验 issuer、audience、签名和 lifetime，不接受 URL token |
| Development 登录 | `/api/v1/auth/password/token` 返回 Basic 凭据，仅用于联调且不校验真实密码 |
| 登录态 | access token、refresh token、PKCE verifier/state 仅存于 `sessionStorage`，不进入 `localStorage`；OIDC 会在 access token 到期前自动续期 |
| 租户 | Production 仅信任 `tenant_id`/`tid` claim；客户端 tenant header 不覆盖 claim，不一致返回 403 |
| Development 兼容 | 仅 Development 的 Basic 认证允许 `X-Tenant-Id` 作为 claim 缺失时的回退，并由 Router 净化后转发 |
| 失败 | 401 清理会话并重新登录；403 保留身份并交由授权界面处理 |
| 授权 | 角色、Agent ACL、能力和租户授权由服务端独立策略处理 |

## 配置

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

生产环境缺少 `Authority`、`Audience` 或 `ClientId` 时启动校验失败；启用 HTTPS metadata 时 Authority
必须为 HTTPS。`Basic` 模式在非 Development 环境同样启动失败。Development 可显式启用匿名兼容，
但默认关闭。

`X-Tenant-Id` 只用于 Development 的 Basic 联调。Production 中该 header 不能建立或覆盖租户身份；
缺少租户 claim 的受保护资源请求会被拒绝，header 与 claim 不一致时返回 403。Router 仅在 Development
向 Engine 转发从认证上下文解析并净化后的租户值，Production 转发始终移除租户 header。

`GET /api/v1/auth/config` 可匿名读取公开登录参数；Basic 密码端点只在 Development + Basic 模式映射。
前端交换 OIDC code 后立即清理回调查询参数；OIDC refresh token 仅用于当前标签页的自动续期，不会进入 `localStorage`、URL、日志或错误详情。退出登录或 refresh token 失效后才要求重新进行交互式登录。

## Source

- Handler: `Backend/src/OpenAgent.Hosting/Security/BasicAuthenticationHandler.cs`
- Registration: `Backend/src/OpenAgent.Hosting/Authentication/AgentAuthenticationExtensions.cs`
- Public config and Development login: `Backend/src/OpenAgent.Hosting/Authentication/AuthenticationEndpointExtensions.cs`
- Context mapping: `Backend/src/OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs`
- Frontend session and PKCE: `Frontend/OpenAgent.Chat/src/api.ts`, `Frontend/OpenAgent.Chat/src/auth.ts`
