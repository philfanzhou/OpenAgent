# 认证

Router 是唯一公开身份边界。生产环境使用 JWT Bearer 对接企业身份提供方；Basic 仅在 Development 环境用于本地联调，其他环境即使收到格式正确的 Basic 凭据也会拒绝。

## 请求边界

1. Router 校验客户端 JWT，读取用户、租户、角色、组和 scope。
2. Router 不向下游转发客户端 `Authorization` header，而是签发 60 秒有效的 HMAC 授权票据。
3. Engine 校验票据签名、issuer、audience 和有效期，并从票据重建只读身份。
4. `X-User-Id`、`X-Tenant-Id` 和外部传入的网关票据不能覆盖已验证身份。

`GET /api/v1/auth/config` 返回当前入口认证模式。Development 模式保留 `POST /api/v1/auth/password/token`；JWT 模式由企业 IdP 负责登录和刷新令牌。

## 部署要求

- Router 设置 `Authentication__Authority`、`Authentication__Audience`。
- Router 与 Engine 通过密钥管理服务注入相同的 `GatewayAuthorization__SigningKey`，密钥至少 32 个字符，禁止写入仓库。
- 每个接收授权票据的第三方 audience 必须配置独立的 `GatewayAuthorization__AudienceSigningKeys__<audience>`；该密钥不能与 Engine 或其他第三方共用。第三方只获得自己的 audience 密钥，不能签发 Engine 票据。
- Engine 不应暴露到公网；即使被直连，没有有效网关票据也无法访问业务端点。
- 浏览器只持有入口 JWT，不会看到内部网关票据。

关键实现位于 `OpenAgent.Authorization`（可复用权限契约）、`OpenAgent.Hosting/Authentication`、`OpenAgent.Hosting/Authorization`（当前 HTTP/HMAC 适配器）和 Router 的转发处理器。
