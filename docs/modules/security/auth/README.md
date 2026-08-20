# Auth

认证只负责建立可验证的请求身份，不负责角色、Agent ACL、能力权限或租户授权决策。

## 当前行为

| 能力 | 行为 |
|---|---|
| Production 登录 | 前端通过企业 IdP 执行 OIDC Authorization Code + PKCE |
| Production API | JWT Bearer 校验 issuer、audience、签名和 lifetime，不接受 URL token |
| Development 登录 | `/api/v1/auth/password/token` 校验内置 `admin/admin`、`test/test` 后返回 Basic 凭据，仅用于联调 |
| 登录态 | token、PKCE verifier/state 仅存于 `sessionStorage`，不进入 `localStorage` |
| 租户 | Router 仅信任 `tenant_id`/`tid` claim；客户端 tenant header 不能建立或覆盖身份 |
| 当前内部转发 | Router 当前保留用户 `Authorization` 和应用 Header，由 Engine 再次认证；尚无独立 Router workload/委托身份 |
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

`X-Tenant-Id`/`X-TenantId` 当前不能建立租户身份；缺少租户 claim 的受保护资源请求会被拒绝，Engine
还会拒绝 Header 与 claim 冲突。Router 当前没有移除这些 Header，YARP 会保留客户端原值，因此下游
必须继续只信任已验证 Claims。该过渡边界和目标委托协议见
[`ADR-0004`](../../../decisions/0004-Authentication-Authorization-Trust-Boundary.md)。

`GET /api/v1/auth/config` 可匿名读取公开登录参数；Basic 密码端点只在 Development + Basic 模式映射。
前端交换 OIDC code 后立即清理回调查询参数，不保存 refresh token，也不会把 token 放入 URL、日志或错误详情。

## Source

- Handler: `Backend/src/OpenAgent.Hosting/Security/BasicAuthenticationHandler.cs`
- Registration: `Backend/src/OpenAgent.Hosting/Authentication/AgentAuthenticationExtensions.cs`
- Public config and Development login: `Backend/src/OpenAgent.Hosting/Authentication/AuthenticationEndpointExtensions.cs`
- Context mapping: `Backend/src/OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs`
- Frontend session and PKCE: `Frontend/OpenAgent.Chat/src/api.ts`, `Frontend/OpenAgent.Chat/src/auth.ts`
