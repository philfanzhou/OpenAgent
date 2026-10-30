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
- `Authorization:Mode=Claims` 使用 PostgreSQL 资源策略及现有身份 scope；`AllowAll` 仅用于显式开发兼容。`Authorization:RequireResourcePolicy=true` 强制启用资源策略，即使 Mode 误配为 AllowAll 也不会放行。
- 先按认证租户读取资源归属和当前配置，再评估显式拒绝、允许规则和 scope；其他租户的同名资源不可用。Admin 只豁免 scope，不豁免归属、显式拒绝或已配置策略中的允许规则。
- 规则按动作匹配（`*` 或动作名，忽略大小写）；UserIds 区分大小写，Roles 不区分大小写，两者为 OR；两者都为空匹配所有主体。拒绝优先；已配置策略没有匹配允许时拒绝。策略允许还须满足原 scope：`agent.admin`、`{resource}.{action}` 或兼容的 `agent.{action}`；Admin 可满足此 scope 检查。
- Agent 目录使用 `execute`，模型目录使用 `invoke`，MCP/Skill 与平台工具发现使用 `use`，平台工具实际调用使用 `invoke`。目录过滤与执行共用 `IResourceAuthorization`；平台工具调用另复核定义资源、父资源、Tool/Function 以及 Agent/模型授权。可以配置“发现允许但调用拒绝”。

### 资源 ID 与策略管理

策略表结构见 [配置表](../database/tables/Configurations.md)。资源 ID：Agent 为 AgentId；Model 为 LLM ProfileId（不再使用 provider/model 拼接值），内嵌模型保留 `@agent/<AgentId>` 命名空间；MCP 为 Name；Skill 为 SkillId；Tool/Function 为平台注册工具名。平台工具没有独立配置行，在当前租户的策略命名空间评估；业务服务继续检查工具参数所指对象的归属。存量内联 MCP/Skill 只从当前租户 Agent 的配置验证，不作为全局资源。

现有 Development 管理映射新增：

- `GET /api/v1/admin/resource-policies/{resourceType}/{resourceId}` 返回策略；未配置时返回 Revision=0、空规则。
- `PUT` 同路径提交 `{ "revision": 0, "rules": [{ "actions": ["use"], "userIds": ["alice"], "roles": [], "deny": false }] }`；Revision=0 创建，之后必须携带当前修订，竞争失败返回 409。空规则表示撤权；没有删除策略回退为宽松规则的端点。
- 仅当前认证租户的 Admin、`agent.admin` 或 `resource.policy.write` 可读写策略，不能由请求体指定其他租户。通用配置写入要求 `agent.config.write` 或 Admin/agent.admin；Development + AllowAll + 非严格模式保留已认证用户的开发配置写入兼容。

### 迁移与回退

先应用 `20261029155656_AddResourcePolicies`，默认 `RequireResourcePolicy=false`：没有策略的既有资源沿用 scope，已有策略立即执行拒绝优先。管理员逐资源安装策略并验证目录和调用矩阵，再将所有实例设为 `RequireResourcePolicy=true`，此时缺少策略一律拒绝。Model 的内嵌与命名连接、Tool 和 Function 均需覆盖；RAG 专属功能和 ACL 继续暂缓。

需要恢复兼容模式时关闭 RequireResourcePolicy 并重启所有实例；已经存在的拒绝策略继续生效。回退到旧二进制会失去本次资源级控制，须先停止入口和活动轮次，保存策略备份，再切回旧版本；可暂留新增表。只有确认退回旧代码且已备份策略后，才将数据库迁移降至 `20261028091823_RemoveCodeExecutionConfiguration`；该 Down 仅删除 resource_policies，不改资源配置。重新升级后恢复策略再开放入口。

### 配置失效与活动撤权

授权读取、Agent/模型凭据解析及执行用 MCP 读取直接访问 PostgreSQL；不使用 TTL 授权缓存。每轮登记已授予资源的配置指纹，启动驱动前与成功提交前复核；活动期每 1 秒复核，单次批量检查预算 2 秒。拒绝、资源删除、配置轮换或权威读取失败都会取消关联轮次，权限失败原因写入历史并经现有 SSE 错误终态返回。取消信号设计预算为 3 秒（轮询间隔 + 检查超时）；进程退出和工具回收沿用 Runtime 的取消机制，不能把信号预算宣称为外部副作用回滚或进程强杀时限。已发往外部 MCP 的请求可能已执行，逐工具网关拦截由 N17.2（#278）完成。

普通 MCP/Runtime Get 缓存使用提交后的 Redis 通知跨实例失效，并保留 30 秒 TTL 与代次检查。通知缺失/Redis 不可用时以读取开始时间计算 TTL，慢读取不能延长旧值的有效期；安全敏感路径始终绕过该缓存。Skill 目录原本直接读库。JWT 中的角色和 scope 仍为本次认证快照；IdP 撤销 token 不在本任务的资源策略撤权保证内。

### 验证

2026-10-30：单元用例覆盖主体/动作、显式拒绝、Admin、兼容/严格默认策略、跨租户、目录后撤权及权威读取失败。真实 Kestrel/SSE + 可控驱动验证撤权会取消驱动、回收枚举器、保存 Failed 与权限错误，客户端收到 error 终态。临时 PostgreSQL 17/Redis 7 的两个独立 ServiceProvider 验证策略 CAS、跨租户、负缓存创建、配置更新、删除通知和轮换取消；一次实测跨实例取消信号 1013 ms（预算 3000 ms）；真实数据库 Down/Up 均成功，模型快照无漂移。这些证据不代表真实 OpenCode 进程或外部 MCP 副作用已回滚。

复现真实存储测试：为一次性数据库和 Redis 设置 `OPENAGENT_TEST_POLICY_POSTGRES` / `OPENAGENT_TEST_POLICY_REDIS`，运行 `dotnet test Backend/tests/OpenAgent.Engine.Tests --filter FullyQualifiedName~ResourcePolicyIntegrationTests`。测试会应用迁移并创建唯一租户；不要指向生产数据库。未设置时仅真实存储用例跳过，HTTP 与单元用例仍执行。

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
