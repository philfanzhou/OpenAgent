# Auth

认证只负责建立可验证的请求身份，不负责角色、Agent ACL、能力权限或租户授权决策。

## 当前行为

| 能力 | 行为 |
|---|---|
| Production 登录 | 前端通过企业 IdP 执行 OIDC Authorization Code + PKCE |
| Production API | JWT Bearer 校验 issuer、audience、签名和 lifetime，不接受 URL token |
| Development 登录 | `/api/v1/auth/password/token` 返回 Basic 凭据，仅用于联调且不校验真实密码 |
| 登录态 | token、PKCE verifier/state 仅存于 `sessionStorage`，不进入 `localStorage` |
| 租户 | Bearer 租户取自受信 claim；客户端 tenant header 不覆盖 claim，不一致返回 403 |
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
    "AllowTenantHeader": false,
    "AllowDevelopmentAnonymous": false,
    "DevelopmentTenantId": "development"
  }
}
```

生产环境缺少 `Authority`、`Audience` 或 `ClientId` 时启动校验失败；启用 HTTPS metadata 时 Authority
必须为 HTTPS。`Basic` 模式在非 Development 环境同样启动失败。Development 可显式启用匿名兼容，
但默认关闭。

`GET /api/v1/auth/config` 可匿名读取公开登录参数；Basic 密码端点只在 Development + Basic 模式映射。
前端交换 OIDC code 后立即清理回调查询参数，不保存 refresh token，也不会把 token 放入 URL、日志或错误详情。

## Source

- Handler: `Backend/src/OpenAgent.Hosting/Security/BasicAuthenticationHandler.cs`
- Registration: `Backend/src/OpenAgent.Hosting/Authentication/AgentAuthenticationExtensions.cs`
- Public config and Development login: `Backend/src/OpenAgent.Hosting/Authentication/AuthenticationEndpointExtensions.cs`
- Context mapping: `Backend/src/OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs`
- Frontend session and PKCE: `Frontend/OpenAgent.Chat/src/api.ts`, `Frontend/OpenAgent.Chat/src/auth.ts`
