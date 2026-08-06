# Auth

认证由 ASP.NET Core Authentication/Authorization 负责，Host 中间件只把已认证 Claims
转换为统一的 `IAgentUserContext`。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| JWT | 生产默认 `Authentication:Mode=JwtBearer`，支持单个 Authority 或多个 `Authentication:Providers` |
| 第三方 SSO | `POST /api/v1/auth/password/token` 代理账号密码登录，SSO 地址必须匹配已配置 Provider |
| 开发免认证 | 仅 Development 且显式开启 `Authentication:AllowDevelopmentPassThrough` 时启用 PassThrough |
| 统一上下文 | UserId、TenantId、Roles、Groups、Audience 从 Claims 转换为 `IAgentUserContext` |

## Architecture
```text
请求 → ASP.NET Authentication → Authorization → AgentUserContextMiddleware → Agent endpoints
        │
   JWT 校验失败 → HTTP 401/403
   Development PassThrough → 使用 DevelopmentUserId/DevelopmentTenantId
```

## Current Status
**Implemented** — 认证模式在 Engine 启动时从环境变量或 appsettings 读取，普通用户不能运行时切换。

## Limits
- 多 Provider JWT 通过 Bearer Token 的 `iss` 选择对应验证 Scheme；每个 Provider 仍由 Authority 完成签名元数据校验
- SSO 地址是前端输入，但后端只允许已配置的 Authority/TokenEndpoint，避免任意 URL 代理请求
- 生产环境不得启用 PassThrough，也不允许通过 `X-Tenant-Id` 覆盖 JWT 中的租户

## Source
- Implementation: `Backend/src/OpenAgent.Hosting/Authentication/AgentAuthenticationExtensions.cs`
- Options: `Backend/src/OpenAgent.Hosting/Authentication/AgentAuthenticationOptions.cs`
- Login endpoints: `Backend/src/OpenAgent.Engine.Host/Extensions/AuthenticationEndpointExtensions.cs`
- Context mapping: `Backend/src/OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs`
