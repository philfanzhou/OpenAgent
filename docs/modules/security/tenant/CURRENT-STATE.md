# 租户链路现状证据

> 基线：`origin/main@fbeccb263c2ca8ba080c5015ecb8ab2679e2e15b`（2026-08-20）。本页只记录代码现状；目标模型见 [DESIGN.md](./DESIGN.md)。

| 链路 | 当前实现证据 | 与目标的差距 |
|------|--------------|--------------|
| Authentication | `OpenAgent.Hosting/Authentication/*` 禁止非 Development 使用 Basic，JWT 校验 issuer/audience/lifetime；`TenantIdentityResolver` 读取 `tenant_id`/`tid` | 无 Tenant/Membership/Status；resolver 只返回 claim，Development header 回退未实现 |
| Router | `JwtUserContextMiddleware` 从 claim 构造上下文；亲和、幂等、查询缓存含 TenantId；YARP 保留原始 header | 环境参数未参与 header 规则；冲突留给 Engine；配置/ACL key 未统一租户化；ACL 缺失/Redis 失败默认允许 |
| Engine Host | `AgentUserContextMiddleware` 再解析 claim/header，`EngineAdmissionMiddleware` 要求聊天认证和 TenantId | 与 Router 重复映射；只校验存在；管理 `HasScope` 当前等同“已认证” |
| AgentUserContext | `IAgentUserContext` 有 UserId、可空 TenantId、roles/groups/claims；Router 用 Items，Engine 用 feature，Hosting 另有 `ICurrentUserContext` | 多套上下文；缺 issuer+sub、成员/租户状态、来源、状态版本和管理员模式 |
| Conversation | record/store/cache/lock 多数操作含 TenantId；列表按当前用户过滤 | PostgreSQL PK 仅 ConversationId，消息/引用表无 TenantId；Infrastructure 隐式依赖当前用户；owner/admin/channel 未模型化 |
| FileAsset | `FileAssetService` 强制 tenant+owner；S3 key 使用 tenant/user hash | repository 按 FileId 单键读取，引用表无 TenantId；缺 OwnerScope、分享/删除/审计字段 |
| Agent | Config 带 TenantId，Engine catalog/runtime 按 tenant 过滤 | Redis、发布索引、本地 store、snapshot、Router ACL 按全局 AgentId；热更新无 tenant |
| Skill | PostgreSQL 复合键、Redis tenant hash、Core catalog 和对象 shared partition 均按 tenant | 最接近目标，但缺成员/状态准入、统一 ResourceKey 和资源审计 |
| LLM | Profile 带 TenantId，Core 调用前复核 profile tenant | Redis/index/内存 registry 按全局 ID；Development 管理 endpoint 调未限定 tenant 的重载 |
| MCP | Profile 带 TenantId，运行时只加载 user tenant server | Redis/index/内存 registry 按全局 name；管理 endpoint 调未限定 tenant 的重载 |
| RAG | Qdrant 可接收 `tenant_id` filter；索引 metadata 补 tenant | RagInstance 无 TenantId，仅 ACL；空 ACL 全开放；RagFlow 忽略 filter；异常吞为无结果而非隔离失败 |
| PostgreSQL | Conversation、FileAsset、SkillDefinition 保存部分 TenantId | 无 Tenant/Membership/Policy/Audit、通用审计字段、完整复合 FK 或 RLS |
| Redis | Conversation/Skill/Router 部分键已租户化 | Agent/LLM/MCP/RAG/ACL/热更新仍用全局 ID，命名/编码/版本不统一 |
| 对象存储 | `S3FileObjectStore` 已按 tenant hash、user/shared scope 分区，读取前有 partition 检查 | 未覆盖通用 OwnerScope、Channel 和租户删除状态机；对象 metadata 不是统一资源键 |
| 前端 | Bearer 请求明确不发 `X-Tenant-Id`，登录后用 `/me` tenant 覆盖本地显示 | Basic 租户只写 localStorage、请求也不发 header；设置页仍暗示客户端可选租户；乐观会话使用本地值 |

## 关键结论

- 当前不存在 Tenant、TenantMembership、TenantPolicy 或租户生命周期聚合；“租户”主要是散落的字符串过滤条件。
- Production JWT 基线方向正确，但 Development header 的目标兼容行为和当前实现/测试不同，不能把文档期望当成已实现。
- Skill 的复合数据库键、Redis tenant hash 和对象 shared partition 可作为其他配置资源迁移参考。
- Conversation/FileAsset 已有服务层隔离，不应在统一过程中删除；应补数据库复合边界和显式 owner/share policy。
- Agent/LLM/MCP/RAG 的全局 ID registry/key 会阻止不同租户安全复用相同 ID，并扩大管理面误读/误写风险。
- RAG 是最高风险缺口：只有部分 adapter 传递 tenant filter，目标架构必须按 adapter 能力 fail closed。
- `/api/v1/admin` 当前只在 Development 映射；上述管理面差距仍需在未来开放生产管理面前修复。

## 主要源码入口

- 身份：`Backend/src/OpenAgent.Hosting/Security/TenantIdentityResolver.cs`、`OpenAgent.Router/Security/JwtUserContextMiddleware.cs`、`OpenAgent.Engine.Host/Middleware/AgentUserContextMiddleware.cs`
- 配置/能力：`Backend/src/OpenAgent.Engine/Config/`、`OpenAgent.Engine/Redis/`、`OpenAgent.Core/Capabilities/`
- 数据：`Backend/src/OpenAgent.Infrastructure/Persistence/OpenAgentDbContext.cs`、`OpenAgent.Infrastructure/Conversations/`、`OpenAgent.Infrastructure/FileAssets/`
- 前端：`Frontend/OpenAgent.Chat/src/api.ts`、`auth.ts`、`App.vue`、`components/LoginPage.vue`
