# ADR-0004：认证、授权与内部信任边界

- 日期：2026-08-20
- 状态：待评审（只定义目标架构，不代表已经实现）
- 复核基线：`canonical/main@aaa3ecee9f28b5b7275b68931ab22a2880c39670`

## 问题

OpenAgent 同时存在浏览器到 Router、Router 到 Engine，以及 Agent 到 Model、Skill、MCP、RAG、Function 的调用。认证、资源授权、委托和工作负载信任必须是可替换但连续的边界，不能把已认证、可路由或来自容器网络误当成已获业务授权。

OIDC 是登录身份层，OAuth 2.0 access token 是 API 凭据，JWT 只是可选令牌格式。API 只接受 access token，不接受 ID token。

## 当前实现证据

| 证据 | 已实现 | 缺口 |
|---|---|---|
| `Backend/src/OpenAgent.Hosting/Authentication/AgentAuthenticationExtensions.cs` | JWT 校验 issuer、audience、签名和 lifetime；Basic 仅允许 Development | 命名 policy 只要求已认证，不表达资源、动作、租户或所有者 |
| `Backend/src/OpenAgent.Hosting/Security/BasicAuthenticationHandler.cs` | Basic 校验内置开发账号；运行配置显式关闭匿名兼容 | 类型默认 `AllowDevelopmentAnonymous=true`，固定凭据不适合非隔离网络 |
| `Backend/src/OpenAgent.Router/Security/JwtUserContextMiddleware.cs` | Router 只从已验证 Claim 建立用户和租户 | 身份事实与授权属性仍混在 `IAgentUserContext` |
| `Backend/src/OpenAgent.Router/Endpoints/ForwardingContextBuilder.cs` | YARP 保留用户 `Authorization` 和客户端身份 Header | Router 没有独立 workload/actor 证明，形成信任混淆 |
| `Backend/src/OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs` | Engine 再从 `ClaimsPrincipal` 建立上下文，租户 Header 不能覆盖 Claim | `X-Agent-Audience` 仍可进入上下文，不能参与未来授权 |
| `Backend/src/OpenAgent.Core/Security/ClaimsAgentAuthorizationService.cs` | 对 Agent、Model、Tool、Function、MCP、Skill 检查 action scope | 不检查 `ResourceId`、tenant、owner、Agent 绑定或委托范围 |
| `Backend/src/OpenAgent.Router/Security/AgentVisibilityService.cs` | Router 提供 Agent 可见性粗筛 | ACL 缺失、为空或 Redis 失败时放行，不能代替 Engine/Core 最终授权 |
| `Backend/src/OpenAgent.Engine.Host/Extensions/ConversationEndpointExtensions.cs` | 详情和删除检查 owner | 列表与搜索只按 tenant 过滤，同租户用户隔离尚未证明 |

`docker-compose.yml` 会把 Engine 端口发布到宿主机。生产网络是否阻止外部直达仍是 `[待确认]`；网络位置本身不能成为服务身份。

## 决策

采用以下分层信任链：

```text
Browser -- access token (aud=router) --> Router
Router -- workload identity + delegated token (aud=engine) --> Engine
Engine/Core -- resource authorization --> Agent capability and persistence
```

1. 浏览器使用 OIDC Authorization Code + PKCE；Router 终止外部 Token。
2. Router 通过 OAuth 2.0 Token Exchange 获取短期、窄 audience、包含 actor 的委托 Token；普通部署至少使用 TLS 和服务身份，高安全部署增加 mTLS 或证书绑定 Token。
3. Router 只做端点权限、Agent 目录和 execute 粗筛；Engine/Core 在加载真实资源后强制 tenant、owner、父子绑定和 action 的最终决策。
4. 授权采用 RBAC 基线、关系 ACL 和 ABAC 的组合，并通过产品无关的 decision 接口接入本地实现或外部 PDP/ReBAC。
5. 认证失败返回 401，已认证但无权返回 403，依赖不可用且不能可信决策时返回 503；未知资源、action 或归属一律 deny。

## 安全上下文与决策合同

目标内部合同拆分为：

- `AuthenticatedIdentity`：issuer、subject、token/client、认证方式、时效和已验证 tenant membership；
- `AuthorizationSubject`：当前 tenant、角色、组、关系和经信任来源归一化的属性；
- `ServiceIdentity`：Router、Engine 或 worker 的 workload 身份；
- `DelegationContext`：subject、actor chain、audience、资源/权限限制和期限；
- `AuthorizationDecision`：allow/deny、reason、policy version、decision ID、obligations 和有效期。

允许结果必须同时满足用户权限、Agent/服务 actor 权限、委托限制以及资源 tenant/owner/parent 不变量。认证成功、目录可见或 Router 放行都不能单独推出最终授权。

## Header 与运行边界

- Router 必须删除客户端 `Authorization`、`X-User-Id`、`X-Tenant-Id`、`X-TenantId`、角色、组和 `X-Agent-Audience`，再写入受控的内部委托凭据。
- `X-Agent-Id`、`X-Conversation-Id` 只用于业务定位；Engine 加载资源后重新授权。
- Trace Header 只用于关联和审计，永不参与 allow。
- Engine 只接受配置的内部 issuer、audience 和 workload；直达外部 Token 或身份 Header 失败关闭。
- Token、API key、Basic credential 和完整敏感 Claim 不进入日志、URL、错误响应或业务文档。

## Development 兼容

Development Basic 仅用于本地联调：必须同时满足 Development 环境、显式 Basic 模式和隔离网络；运行配置默认关闭匿名兼容。内置账号不能映射生产身份或隐式平台管理员。Production/Staging 遇到 Basic、AllowAll、匿名、空 issuer/audience 或明文内部凭据时应启动失败。

## 迁移顺序

1. 定义安全上下文、结构化 decision 和生产默认 deny；
2. 规范 Claims、fallback policy、Basic/匿名启动校验和 Header 清洗；
3. 引入 Engine 专属 audience、workload 身份和受控委托；
4. 实现本地 RBAC + 资源关系/ABAC adapter，再单独评估外部 PDP/ReBAC；
5. 对 Agent、Model、Skill、MCP、RAG、Function、Conversation 做发现和调用双阶段授权；
6. 补撤销、decision audit、缓存失效、readiness、跨租户和故障注入验证。

待确认：目标 IdP 的 RFC 8693/RFC 8705 能力、是否采用 BFF、共享会话与平台公共资源语义，以及授权服务 SLO。

## 参考

- [RFC 8693：OAuth 2.0 Token Exchange](https://www.rfc-editor.org/rfc/rfc8693.html)
- [RFC 8705：OAuth 2.0 Mutual TLS](https://www.rfc-editor.org/rfc/rfc8705.html)
- [RFC 8725：JWT Best Current Practices](https://www.rfc-editor.org/rfc/rfc8725.html)
- [RFC 9700：OAuth 2.0 Security Best Current Practice](https://www.rfc-editor.org/rfc/rfc9700.html)
- [ASP.NET Core resource-based authorization](https://learn.microsoft.com/aspnet/core/security/authorization/resource-based?view=aspnetcore-8.0)
- [NIST SP 800-207A：Cloud-Native Zero Trust](https://csrc.nist.gov/pubs/sp/800/207/a/final)
