# Auth

当前认证只负责从基础账号密码建立请求身份，不负责用户目录查询、密码正确性校验或资源授权。

## 当前行为

| 能力 | 行为 |
|---|---|
| 登录 | `POST /api/v1/auth/password/token` 接收账号密码并返回 Basic 凭据 |
| 认证 | 后端解析 `Authorization: Basic ...`，任意格式正确的账号密码均可通过 |
| 用户身份 | Basic 用户名作为 `UserId`，租户从 `X-Tenant-Id` 或默认配置取得 |
| 开发免登录 | Development 环境且 `Authentication:AllowDevelopmentAnonymous=true` 时使用默认开发用户 |
| 授权 | 当前策略只要求已认证，资源级授权后续单独实现 |

## 配置

```json
{
  "Authentication": {
    "Mode": "Basic",
    "AllowTenantHeader": true,
    "AllowDevelopmentAnonymous": false,
    "DevelopmentUserId": "development-user",
    "DevelopmentTenantId": "development"
  }
}
```

生产环境应关闭 `AllowDevelopmentAnonymous`。当前 Basic 凭据本身不包含签名，适用于联调和功能开发，
不应作为生产环境的最终安全方案。

## Source

- Handler: `Backend/src/OpenAgent.Hosting/Security/BasicAuthenticationHandler.cs`
- Registration: `Backend/src/OpenAgent.Hosting/Authentication/AgentAuthenticationExtensions.cs`
- Login endpoint: `Backend/src/OpenAgent.Engine.Host/Extensions/AuthenticationEndpointExtensions.cs`
- Context mapping: `Backend/src/OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs`
