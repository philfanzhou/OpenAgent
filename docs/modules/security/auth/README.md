# 认证

认证只负责建立可验证的请求身份，不负责角色、Agent ACL、能力权限或租户授权决策。

## 请求边界

| 能力 | 行为 |
|---|---|
| Production 登录 | 前端通过企业 IdP 执行 OIDC Authorization Code + PKCE |
| Production API | JWT Bearer 校验 issuer、audience、签名和 lifetime，不接受 URL token |
| Development 登录 | `/api/v1/auth/password/token` 校验内置 `admin/admin`、`test/test`，仅用于本地联调 |
| 登录态 | token、PKCE verifier/state 仅存于 `sessionStorage`，不进入 `localStorage` |
| 租户 | Production 仅信任 `tenant_id`/`tid` claim；客户端 tenant header 不覆盖 claim，不一致返回 403 |
| Development 租户 | Basic 身份使用服务端 `DevelopmentTenantId`；客户端 tenant header 只能参与冲突检测，不能建立身份 |
| Router → Engine | Router 删除客户端身份、路由与 Authorization header，改发短时签名 grant；Engine 使用 `Gateway` 模式验证 |
| 失败 | 401 清理会话并重新登录；403 保留身份并交由授权界面处理 |
| 授权 | 角色、Agent ACL、能力和租户授权由服务端独立策略处理 |

`GET /api/v1/auth/config` 返回当前入口认证模式。Development 模式保留 `POST /api/v1/auth/password/token`；JWT 模式由企业 IdP 负责登录和刷新令牌。

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

`X-Tenant-Id` 不能建立或覆盖租户身份；缺少租户 claim 的受保护资源请求会被拒绝，header 与 claim
不一致时返回 403。Router 向 Engine 转发从认证上下文解析并净化后的租户值，同时移除客户端提供的
`Authorization`、identity、routing 和 gateway grant header。

Router 以 HMAC-SHA256 签发短时 `X-OpenAgent-Gateway-Grant`，Engine 只接受匹配 issuer、audience、签名和
有效期的 grant。不同 audience 必须使用不同密钥。密钥由部署环境注入；仓库中的 Development 密钥不能
用于共享或生产环境。当前实现尚未提供 grant replay store、密钥 ID/滚动轮换或传输层 HTTPS 强制，部署
边界仍必须用 TLS/mTLS 或等价的受控服务网络保护。

`GET /api/v1/auth/config` 可匿名读取公开登录参数；Basic 密码端点只在 Development + Basic 模式映射。
前端交换 OIDC code 后立即清理回调查询参数，不保存 refresh token，也不会把 token 放入 URL、日志或错误详情。

## Source

- Handler: `Backend/src/OpenAgent.Hosting/Security/BasicAuthenticationHandler.cs`
- Registration: `Backend/src/OpenAgent.Hosting/Authentication/AgentAuthenticationExtensions.cs`
- Gateway validation: `Backend/src/OpenAgent.Hosting/Authorization/GatewayGrantCodec.cs`
- Public config and Development login: `Backend/src/OpenAgent.Hosting/Authentication/AuthenticationEndpointExtensions.cs`
- Context mapping: `Backend/src/OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs`
- Frontend session and PKCE: `Frontend/OpenAgent.Chat/src/api.ts`, `Frontend/OpenAgent.Chat/src/auth.ts`
