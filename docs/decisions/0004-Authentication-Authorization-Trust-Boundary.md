# ADR-0004：认证、授权与内部信任边界

- 日期：2026-08-20
- 状态：待评审（本 ADR 只定义目标架构，不实施迁移）
- 基线：`origin/main@fbeccb263c2ca8ba080c5015ecb8ab2679e2e15b`

## 背景与范围

OpenAgent 同时存在浏览器到 Router、Router 到 Engine、Agent 到 Model/Skill/MCP/RAG/Function 等调用。当前代码已具备 Development Basic、JWT Bearer、`IAgentUserContext`、Claims 授权、Agent/RAG ACL 和部分租户/所有者校验，但这些机制尚未形成一致的信任链。

本 ADR 区分并设计以下六个概念：

| 概念 | 定义 | 不负责 |
|---|---|---|
| 认证（Authentication） | 验证用户、客户端或工作负载是谁，以及凭据是否有效 | 判断其能访问哪个业务资源 |
| 身份上下文（Identity Context） | 保存已验证的 issuer、subject、tenant membership、认证方式、时效和原始声明来源 | 直接等同权限 |
| 授权主体（Authorization Subject） | 参与决策的用户、服务、Agent，以及其角色、组、关系和属性 | 证明凭据真实性 |
| 授权决策（Authorization Decision） | 对 subject/actor、resource、action、context 返回 allow/deny、原因、策略版本和义务 | 签发令牌 |
| 委托授权（Delegation） | 表达 Router/Agent 代表用户执行的有限权限、actor 链、受众和期限 | 仅凭转发用户 Header 建立身份 |
| 内部服务信任（Workload Trust） | 通过服务 Token 和/或 mTLS 证明调用者是获准的 Router 工作负载 | 自动信任该服务携带的用户数据 |

OIDC 是登录身份层，OAuth 2.0 access token 是 API 授权凭据，JWT 只是可选的令牌格式；三者不得混称。API 只接受 access token，不接受 ID token。

## 当前代码证据与风险

| 证据 | 当前行为 | 风险评估 |
|---|---|---|
| `Backend/src/OpenAgent.Hosting/Security/BasicAuthenticationHandler.cs`、`Backend/src/OpenAgent.Hosting/Authentication/DevelopmentCredentials.cs` | 仅 `Development` 可用；内置 `admin/admin`、`test/test`；无 Header 时可按配置建立默认开发身份；密码端点返回可重放的 Basic 凭据 | Basic 仅 Base64 编码且凭据固定；`AllowDevelopmentAnonymous` 类型默认值为 `true`，若开发实例暴露到非本机网络会扩大风险 |
| `Backend/src/OpenAgent.Hosting/Authentication/AgentAuthenticationExtensions.cs` | JWT 校验 issuer、audience、签名、lifetime，关闭 inbound claim mapping；注册的 ASP.NET Core policy 只要求已认证 | 认证基础正确，但 `ClientId` 不参与 API 调用方约束，策略名目前不表达资源、动作、租户或所有者 |
| `Backend/src/OpenAgent.Router/Security/JwtUserContextMiddleware.cs` | Router 只从已认证 Claims 解析用户和租户；客户端 tenant Header 不能建立或覆盖 Router 身份 | user 缺失回退为 `unknown`；角色/Claim 归一化不一致；该上下文同时承载身份事实和授权属性 |
| `Backend/src/OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs` | Engine 再从自己的 `ClaimsPrincipal` 建立上下文；tenant Header 只能触发冲突，不能建立租户；`X-Agent-Audience` 可写入上下文 | 身份上下文仍可被未认证的业务 Header 补充；未来策略若使用 `Audience`，会形成权限提升入口 |
| `Backend/src/OpenAgent.Router/Endpoints/ForwardingContextBuilder.cs` 与 `Backend/tests/OpenAgent.Router.Tests/Endpoints/GatewayProxyHandlerTests.cs` | YARP 保留客户端原始 `Authorization`、`X-User-Id`、`X-Tenant-Id`、`X-Agent-Id` 等 Header | 外部 bearer token 被直接传给 Engine，Router 没有独立服务身份或 actor 证明；身份 Header 虽暂不能覆盖 Claims，但构成易误用的信任混淆 |
| `Backend/src/OpenAgent.Router/Providers/OpenAgentEngineProvider.cs` | Provider 可配置任意 `ServiceHeaders`；存在请求上下文时，原始用户 `Authorization` 覆盖服务 Authorization | 同一路径混用用户 Token 与静态服务凭据，无法稳定区分用户、Router 和委托 actor |
| `Backend/src/OpenAgent.Core/Security/AgentAuthorizationOptions.cs`、`Backend/src/OpenAgent.Core/Exten/RuntimeServiceExtensions.cs` | 授权类型默认 `AllowAll`；生产 appsettings 显式为 `Claims`，Development 为 `AllowAll` | 配置遗漏或宿主复用时会失败放开；`AllowAll` 甚至不检查 `IsAuthenticated` |
| `Backend/src/OpenAgent.Core/Security/ClaimsAgentAuthorizationService.cs` | `Admin` 全放行；scope 只按资源类型和 action 匹配，`agent.{action}` 可覆盖所有资源 | 不校验 `ResourceId`、租户、所有者、Agent 绑定或委托范围；Claim 拼接/大小写处理在 Router 与 Engine 间不一致 |
| `Backend/src/OpenAgent.Router/Security/AgentVisibilityService.cs` | Agent ACL 采用 user/group/tenant/role 任一匹配；无 ACL、空 ACL 或 Redis 读取失败时放行 | Router 可见性 ACL 失败开放且不是 Engine 最终授权；同租户任一用户可因 tenant 条目获得访问 |
| `Backend/src/OpenAgent.Core/Security/AgentAuthorizationGate.cs` 及能力工厂 | Agent/Model 和 Tool/Function/MCP/Skill 在解析或发现阶段经过 Gate；部分资源另做租户过滤 | 当前枚举没有 RAG、Conversation、Tenant、User；能力包装器调用时没有统一的二次决策，存在策略变化后的 TOCTOU 窗口 |
| `Backend/src/OpenAgent.Core/Capabilities/Rag/RagService.cs` | RAG 实例 ACL 为空时放行，四类主体任一匹配；检索只附加 tenant 过滤；写入 metadata 可保留调用方给出的 `tenant_id` | RAG ACL 与统一 Gate 分离；文档级 ACL、写入租户覆盖和决策审计不足 |
| `Backend/src/OpenAgent.Engine.Host/Extensions/ConversationEndpointExtensions.cs` | 单会话读取/删除校验 owner；列表和搜索只按 tenant 查询 | [推断] 同租户用户之间可能看见会话列表或搜索结果，需由资源级策略和查询过滤共同关闭 |

`docker-compose.yml` 还将 Engine 端口发布到宿主机。[待确认] 生产部署是否通过网络策略禁止外部直达 Engine；无论网络是否隔离，都不能把网络位置本身当成服务身份。

## 方案比较

### 身份与 Router → Engine 委托

| 方案 | 优点 | 局限 | 结论 |
|---|---|---|---|
| 同一用户 JWT 透传 | 改动小，Router/Engine 可离线验签 | 同一 bearer 可重放；无法表达 Router actor；共享 audience 会扩大泄漏半径；Engine 直达可绕过 Router 粗授权 | 仅可作为限时迁移态 |
| Router 自签内部 JWT | 可区分内部 audience 与 actor，性能好 | Router 变成自建授权服务器，承担签发、密钥、撤销和 confused deputy 风险 | 不作为长期方案 |
| OAuth 2.0 Token Exchange + 服务认证 | IdP/STS 签发窄 audience、短期、含 `act` 的委托 Token；用户与 actor 可审计 | 依赖 IdP 支持 RFC 8693，增加一次兑换与缓存 | 推荐的委托语义 |
| 仅 mTLS + 身份 Header | 服务身份强、无需 Token 兑换 | mTLS 不携带用户授权；Header 规范化、签名和代理信任复杂 | 不能单独承担委托 |
| Token Exchange + mTLS/证书绑定 Token | 同时证明用户委托和 Router 工作负载，降低 Token 被盗后的重放 | 证书自动化和网格/PKI 运维成本最高 | 高安全环境推荐；普通部署至少使用服务 Token + TLS |

浏览器登录采用 OIDC Authorization Code + PKCE；不使用 Implicit 或 Resource Owner Password Credentials。纯服务任务可用 OAuth client credentials，但 client credentials 不能伪装成用户委托。

### 授权模型与 Policy Service

| 方案 | 适用边界 | 不适用边界 |
|---|---|---|
| Claims/RBAC | Router 端点粗授权、租户级管理角色、稳定且低基数的权限 | 单资源共享、所有权、层级继承、动态环境条件；不应把完整 ACL 塞入长寿命 Token |
| 应用内资源授权 | tenant/owner/Agent 绑定等不可绕过的领域不变量；第一阶段低延迟落地 | 多服务策略一致性、集中策略发布和复杂关系查询 |
| ABAC/PDP（如 OPA 类） | subject/resource/action/environment 的动态规则、集中策略版本、决策日志 | 自身不负责维护资源关系事实；远程 PDP 是可用性依赖 |
| ReBAC/关系服务（如 OpenFGA 类） | 用户/组/租户/Agent/会话/RAG 文档的 owner、member、viewer、继承关系 | 配额、网络风险、认证强度等动态条件不能只靠关系图 |

推荐混合模型：RBAC 提供租户基线角色；关系 ACL 表达资源共享和继承；ABAC 处理认证强度、时间、环境、数据分级和副作用；Engine 内部仍强制 tenant、owner、绑定关系等领域不变量。通过产品无关的授权决策接口接入本地实现或外部 PDP，具体 Policy Service 产品另行 ADR 决定。

## 目标信任链

```text
Browser -- OIDC Code+PKCE --> IdP
Browser -- access token (aud=router) --> Router PEP
  Router: 验签 + tenant membership + endpoint/Agent 可见性粗授权
  Router -- token exchange(subject=user, actor=router) --> IdP/STS
  Router == mTLS + delegated token (aud=engine) ==> Engine PEP
    Engine: 验证 workload + delegation，重建可信请求上下文
    Engine/Core: tenant/owner 不变量 + 资源级授权决策
      Agent -- workload/connector credential --> Model/MCP/RAG/Function
```

Engine 是其数据和运行能力的最终 Resource Server。Router 的 allow 只表示“可以尝试路由”，不能替代 Engine/Core 的最终 allow。Engine 不应公开到外部入口；网络策略是纵深防御，不是认证机制。

### 目标请求安全上下文

现有 `IAgentUserContext` 可作为迁移适配器，但目标内部合同应拆分为：

- `AuthenticatedIdentity`：`Issuer`、稳定 `SubjectId`、`TokenId`、`ClientId/Azp`、认证方式与强度、`IssuedAt/ExpiresAt`、已验证 tenant membership；
- `AuthorizationSubject`：用户 ID、当前 tenant、角色、组及经信任属性源归一化的属性；原始 Claims 只作输入，不直接成为 allow；
- `ServiceIdentity`：Router/Engine/worker 的 workload ID、凭据类型和证书/Token 绑定；
- `DelegationContext`：原始 subject、当前 actor、actor chain、scopes/actions、resource/audience 限制和过期时间；
- `RequestSecurityContext`：以上信息加 trace、session、网络/设备等决策上下文，不包含明文 Token。

若用户可加入多个租户，“当前租户”必须来自 IdP 签发的租户专用 access token、Token Exchange 结果或服务端已验证的 membership/session；客户端 `X-Tenant-Id` 只能作为选择请求，不能成为身份事实。

### 授权决策合同

将当前 bool 型请求演进为语义完整且可审计的合同：

```text
Check(subject, actorChain, resource{type,id,tenant,owner,parent}, action,
      context{session,authStrength,risk,requestTime})
  -> decision{allow,reasonCode,policyId,policyVersion,decisionId,obligations,expiresAt}
```

决策必须满足以下交集，而不是任一条件命中即放行：

```text
Allow = 已认证用户
     AND 活跃租户成员
     AND 用户对资源/动作有权限
     AND Agent/服务 actor 对资源/动作有权限
     AND 委托 Token 的 audience/scope/resource/actor/expiry 覆盖本次调用
     AND 资源 tenant/owner/parent 等领域不变量成立
     AND 环境义务已满足
```

`obligations` 可表达脱敏、最大结果数、只读、需要人工批准、指定 Model、禁止外部网络等约束。未知资源类型、未知 action、缺少资源属性或冲突属性一律 deny。

## 资源与 ACL 模型

所有资源拥有不可为空的 `TenantId`（平台公共模板需使用显式 platform tenant/visibility，而不是空字符串）。默认私有；“无 ACL”不等于公开。公开资源必须有显式 `public/discoverable` 策略。

| 资源 | 典型 action | 强制约束与关系 |
|---|---|---|
| User | `read-self`、`update-self`、`disable`、`impersonate` | `iss+sub` 组成稳定主键；跨用户操作需 tenant admin 且单独审计；默认禁止 impersonate |
| Tenant | `enter`、`read`、`manage-members`、`manage-policy`、`read-audit` | 用户必须有活跃 membership；tenant admin 只在本 tenant 生效，不能成为平台全局 Admin |
| Agent | `discover`、`read`、`execute`、`configure`、`publish`、`delegate` | owner/editor/viewer/executor 关系；Router 只做 discover/execute 粗筛，Engine 在解析配置和执行前复核 |
| Model | `discover`、`invoke`、`configure`、`use-credential` | tenant 一致；Agent→Model 显式绑定；用户权限、Agent actor 权限、配额/数据分级同时满足；凭据永不下发给用户或 Agent 提示词 |
| Skill | `discover`、`load`、`read-resource`、`invoke`、`manage` | tenant + Agent 绑定 + 包完整性；加载和实际调用均复核；脚本执行另需 sandbox/approval 义务 |
| MCP | `connect`、`list-tools`、`invoke-tool`、`manage` | Server 与 Tool 分别建资源；校验 tenant、Agent 绑定、出站目标和凭据；每次有副作用调用前复核 |
| RAG | `query`、`read-document`、`index`、`delete`、`manage` | Instance、collection/document 分层；检索前过滤授权集合、返回前防御性复核；服务端覆盖 tenant metadata，禁止调用方指定其他租户 |
| Function | `discover`、`invoke` | 每个 Function 是独立资源，标注 read/write/external/privileged 风险；发现不代表可调用，高风险调用需审批或 step-up auth |
| Conversation | `create`、`read`、`append`、`delete`、`share` | tenant + owner/member；列表、搜索、读取、写入使用同一授权过滤；会话绑定的 Agent 不能被请求 Header 越权替换 |

Capability 是 Skill、MCP Tool、RAG 操作和 Function 的聚合视图。一次调用必须同时通过父资源、叶子资源、Agent 绑定和 action 决策。File 等会话附件默认继承 Conversation tenant/owner/member 关系。

## 分层执行职责

| 层 | 必须执行 | 不得执行 |
|---|---|---|
| Router 粗授权 | 校验外部 access token；确定可信 subject/tenant；端点 scope；批量过滤可发现 Agent；限制请求大小/频率；签发或兑换内部委托；清洗 Header | 根据客户端身份 Header 建上下文；把目录可见性当最终执行许可；读取 Model/MCP 密钥；在 PDP 不可用时放行 |
| Engine 入口 | 同时验证 Router workload 和 `aud=engine` 委托 Token；校验 subject/actor/tenant 一致；拒绝直达外部 Token 和不可信 Header | 仅因来源 IP、容器网络或 Router 已 allow 就信任请求 |
| Engine/Core 精授权 | 资源加载后检查 tenant/owner/parent；Agent/Model/Capability/Conversation 每个 action 授权；发现和调用双阶段复核；对存储查询下推授权过滤 | 依赖 UI 隐藏、Router ACL 或空 ACL；把异常吞掉后返回 allow |
| 下游连接器 | 使用自身工作负载/资源凭据；执行 egress、最小权限和结果过滤；记录外部决策关联 ID | 转发用户或 Router access token 给 Model/MCP/RAG，除非下游明确参与受众受限的标准委托 |

Router 和 Engine 应使用同一策略语义和 policy version，但 Engine 始终重算最终决策。Router 可用 batch-check/list-objects 降低 Agent 目录开销；缓存键必须包含 tenant、subject/actor、resource/action、policy version，并受短 TTL 约束。

## 内部 Header 与信任条件

| Header/数据 | Router 行为 | Engine 行为 |
|---|---|---|
| `Authorization` | 终止外部 Token；下游请求删除原值并写入 `aud=engine` 委托 Token | 只接受配置的内部 token type/issuer/audience；拒绝 Router audience 的外部 Token |
| `X-User-Id`、`X-Tenant-Id`、`X-TenantId`、角色/组 Header | 入站先删除；不得用于签发身份 | 默认拒绝或忽略，不建立身份；迁移期若保留只能与签名 Token 完全一致 |
| `X-Agent-Audience` | 删除；OAuth `aud` 只来自已验证 Token | 不读取为身份或授权属性 |
| `X-Agent-Id` | 解析、规范化为业务资源选择；不声明权限 | 加载 Agent 后按 tenant/execute 决策，不信任其归属 |
| `X-Conversation-Id` | 作为业务定位并限制格式/长度 | 加载会话后校验 tenant/owner/member/Agent 绑定 |
| `traceparent`/`X-Trace-Id` | 校验格式和长度后传播，必要时生成新值 | 仅用于关联与审计，永不参与 allow |
| 代理转发的客户端证书 Header | 仅在固定 trusted proxy、TLS 链路且边缘覆盖原值时可用 | 优先使用 TLS 连接证书；若转发证书，必须配置 known proxy 并在最外层剥离客户端同名 Header |

只有同时满足以下条件，Engine 才能信任 Router 提交的委托上下文：

1. 连接通过 TLS；委托 Token 的 `act`/`azp` 来自 STS 对 Router client 的认证，高安全部署再以 mTLS 或证书绑定 Token 证明持有者；
2. 委托 Token 的签名、固定算法、`typ`、issuer、`aud=engine`、`iat/nbf/exp`、`jti` 全部有效；
3. Token 的 `act`/authorized party 与连接上的 workload ID 一致，且 Router 被允许调用目标 Engine；
4. tenant 和用户来自 Token/受信属性源，资源 tenant 由 Engine 加载，两者一致；
5. 所有可能表达身份的外部 Header 已在 Router 覆盖或删除，Engine 对直达请求失败关闭。

## Token、密钥与证书生命周期

- 外部 access token 建议不超过 5–15 分钟；内部委托 Token 建议不超过 1–5 分钟，且不得长于原 Token；clock skew 基线不超过 60 秒。实际值由威胁模型和 IdP 能力确认。
- Refresh token 只保存在 OIDC 客户端/BFF 与 IdP 之间，启用 rotation 和 reuse detection，不进入 Router、Engine、日志、URL 或浏览器长期存储。
- JWT 使用非对称签名、固定算法 allowlist、显式 `typ` 和服务专属 audience。通过受信 discovery/JWKS 获取 key；发布新 key 后再签发，旧 key 至少保留到最长 Token lifetime + skew 结束，再撤下。
- 未知 `kid` 触发一次受限 JWKS refresh；metadata/JWKS 刷新失败只能继续使用未过期的最后可信配置，超过 freshness 上限后 readiness 失败并拒绝新请求，不能接受任意 `jku/x5u`。
- Router/Engine 服务凭据存入受管 Secret/KMS，不写 `ServiceHeaders` 明文配置。服务 Token 不使用 refresh token；mTLS 证书自动签发和轮换，建议生命周期不超过 24 小时并在过半前更新，轮换期允许新旧证书短暂重叠。
- 用户登出或授权撤销按 RFC 7009 撤销 refresh/grant；离线 JWT 的即时撤销通过短 TTL 控制。高风险管理调用额外使用 RFC 7662 introspection、分布式 `jti` denylist 或 subject/tenant `authorization_epoch`。
- ACL、membership、资源状态和策略变更由 PDP/PIP 实时读取或以版本化事件失效缓存；高风险 action 不使用仅靠 Token 内旧角色的长缓存。

## 审计、失败关闭与隐私

每次认证记录 issuer、subject 的不可逆标识、tenant、client/workload、认证方式、结果和 reason code；绝不记录 Token、Basic credential、API key、完整证书或敏感 Claim。每次授权记录 `decisionId`、subject、actor chain、tenant、resource type/id、action、allow/deny、reason、policy id/version、obligations、PDP latency、trace/session ID。ACL、membership、策略、密钥和高风险资源配置的变更还需记录操作者、前后版本和审批信息。

以下情况必须失败关闭：生产启用 Basic/AllowAll/匿名；凭据或 tenant 缺失/冲突；issuer/audience/算法/类型/actor 不匹配；身份 Header 来自非可信链路；ACL/PDP 读取、解析或超时；策略版本未知或缓存超过 freshness；资源 tenant/owner 不可确定；未知 action/resource。认证失败返回 401，已认证但无权返回 403；目录和按 ID 查询可按防枚举策略返回 404；依赖不可用且无法作出可信决策返回 503，并记录 deny/error 而不是 allow。

健康检查仅暴露最小信息且可匿名；管理和业务端点采用 fallback policy 默认要求认证。授权系统不可用时服务应从 readiness 摘除。

## Development 兼容策略

Development Basic 只作为显式兼容模式保留，不代表生产身份系统：

- `ASPNETCORE_ENVIRONMENT=Development`、显式 `Mode=Basic`、显式启用开关和 loopback/隔离网络四项同时满足；目标默认 `AllowDevelopmentAnonymous=false`；
- 固定开发账号只映射到固定 development tenant 和受限 `Developer` 角色，不映射生产 Admin；响应与启动日志明确标记不安全开发模式；
- Router 仍是默认入口。Engine 直达仅允许本机调试，并使用独立的开发服务身份；不得把客户端 Basic 或身份 Header 当作生产内部协议；
- Development 可使用本地临时签名 key 生成数分钟的内部委托 Token，进程重启即失效；不得与生产 issuer、audience、key 或 Secret 共用；
- Production/Staging 遇到 Basic、AllowAll、匿名、HTTP metadata、空 issuer/audience、静态内部 Authorization Header 时启动失败。

## 决策与后续拆分

本 ADR 推荐：外部 OIDC/OAuth 2.0 + 服务专属 JWT audience；Router 使用 Token Exchange 取得 `aud=engine`、含 Router actor 的短期委托 Token；Router→Engine 使用 TLS，高安全部署使用 mTLS/证书绑定 Token；纯服务调用才使用 client credentials Token；Router 粗授权、Engine/Core 精授权；RBAC + 关系 ACL + ABAC 混合模型；所有授权依赖失败关闭。

本 ADR 不选择具体 IdP、PDP/ReBAC 产品，也不授权本 PR 修改运行时代码。后续按可回滚边界拆分：

1. 安全上下文与授权决策合同：补齐 Tenant/User/RAG/Conversation 等资源类型、actor/delegation、结构化 decision，生产默认 deny；
2. Router 外部认证硬化：规范 Claims、fallback policy、Basic/匿名/loopback 启动校验和 Header 清洗；
3. 内部信任：IdP Token Exchange（或受控迁移适配器）、Engine 专属 audience、workload Token/mTLS、网络入口限制及密钥轮换；
4. Policy 实现：先落地应用内 RBAC + 资源关系/ABAC adapter，再对 OPA/OpenFGA 等候选做独立 ADR 和故障演练；
5. Engine/Core 精授权：Agent/Model/Skill/MCP/RAG/Function 发现与调用双检、Conversation 查询/owner/share、RAG 文档过滤与 metadata 覆盖；
6. 安全运营：撤销/introspection、decision audit、缓存失效、readiness、跨租户/重放/故障注入测试和分阶段切流。

待确认事项：目标 IdP 是否支持 RFC 8693 与证书绑定 Token；是否采用 BFF 降低浏览器 Token 暴露；共享会话的产品语义；平台公共资源的 tenant 模型；高风险 Function 的审批体验；Policy Service 的一致性、延迟和可用性 SLO。

## 参考

- [OpenID Connect Core 1.0](https://openid.net/specs/openid-connect-core-1_0.html)
- [RFC 9068：OAuth 2.0 Access Token 的 JWT Profile](https://www.rfc-editor.org/rfc/rfc9068.html)
- [RFC 9700：OAuth 2.0 Security Best Current Practice](https://www.rfc-editor.org/rfc/rfc9700.html)
- [RFC 8693：OAuth 2.0 Token Exchange](https://www.rfc-editor.org/rfc/rfc8693.html)
- [RFC 8705：OAuth 2.0 Mutual TLS 与证书绑定 Token](https://www.rfc-editor.org/rfc/rfc8705.html)
- [RFC 8725：JWT Best Current Practices](https://www.rfc-editor.org/rfc/rfc8725.html)
- [RFC 7009：OAuth 2.0 Token Revocation](https://www.rfc-editor.org/rfc/rfc7009.html)
- [RFC 7662：OAuth 2.0 Token Introspection](https://www.rfc-editor.org/rfc/rfc7662.html)
- [ASP.NET Core policy-based authorization](https://learn.microsoft.com/aspnet/core/security/authorization/policies?view=aspnetcore-8.0)
- [ASP.NET Core resource-based authorization](https://learn.microsoft.com/aspnet/core/security/authorization/resource-based?view=aspnetcore-8.0)
- [NIST SP 800-162：ABAC](https://csrc.nist.gov/pubs/sp/800/162/upd2/final)
- [NIST SP 800-207A：Cloud-Native Zero Trust](https://csrc.nist.gov/pubs/sp/800/207/a/final)
- [Open Policy Agent：Decision Logs](https://www.openpolicyagent.org/docs/management-decision-logs)
- [OpenFGA：Authorization Model Concepts](https://openfga.dev/docs/concepts)
