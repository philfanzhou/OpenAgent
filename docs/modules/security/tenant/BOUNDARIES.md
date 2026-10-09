# 租户资源与组件边界

> 依赖：[租户概念模型](./DESIGN.md)

资源只有 `Platform` 与 `Tenant` 两种顶层作用域。租户资源用 `TenantResourceKey(TenantId, ResourceType, ResourceId)` 标识，并用 `OwnerScope = User | Tenant | Channel | Service` 表达租户内共享；ACL 只能在同一 TenantId 内生效。

## 资源归属与共享

| 资源 | 目标归属/默认共享 | 跨租户与管理员规则 |
|------|-------------------|--------------------|
| Tenant/Membership/Policy | 租户控制面 | 不可跨租户引用；PlatformAdmin 走显式平台接口 |
| Agent | Tenant，租户内发布并可追加 ACL | 不能绑定其他租户能力；平台模板只能实例化/复制 |
| LLM/MCP Profile | Tenant，tenant-shared config/secret | 相同 endpoint/URL 也建立独立 profile 和 secret |
| RAG Instance/索引 | Tenant；外部检索必须 tenant filter | 不能证明过滤的 adapter fail closed |
| Skill Definition/Package | Tenant，默认 tenant-shared | Agent 只绑定同租户 Skill；平台模板复制后使用 |
| User Conversation | Tenant + User，默认私有 | Owner/Admin 不自动读取正文；合规读取需专门权限和审计 |
| Internal Conversation | Tenant + Service | 仅绑定 service 继续；Admin 可停用但不自动读取正文 |
| Channel Conversation | Tenant + ChannelBinding/Thread | 一个 thread 只能映射一个租户 |
| Message | 继承 Conversation | 子记录 FK 必须包含 TenantId，无独立越权入口 |
| FileAsset | Tenant；默认 User，可显式变为 Conversation/Channel/Tenant shared | 引用两端 TenantId 必须一致；对象键不授权 |
| Router cache/lock/affinity | tenant + subject/resource 的派生数据 | 禁止空 tenant key，不设管理员绕过 |
| Engine registry/health | Platform | 不承载租户业务数据 |
| AuditEvent | Tenant；平台事件使用 Platform scope | PlatformAdmin 按显式目标和理由查询 |

禁止提供仅 `resourceId` 的租户 repository/registry/cache API；后台任务、热更新和重试必须保留最初 TenantId。跨租户复制需创建新资源、重新加密并记录来源，不能原地共享 secret、对象或 RAG 文档。

跨租户管理只能使用 `/api/v1/platform/tenants/{tenantId}/...` 等独立控制面，要求 `PlatformAdmin`、显式目标、非空理由、短时 step-up 授权和不可变审计。`AccessMode=PlatformAdministration` 不等同 impersonation；会话/文件正文另需 `ComplianceReader` 和租户策略允许。

## 组件职责

| 组件 | 租户职责 | 禁止 |
|------|----------|------|
| Authentication/Hosting | 验证 JWT，统一 claim/header 规则，构造 subject | 从请求体推断租户 |
| Router | 第一层准入，租户化限流/缓存/幂等/亲和，清理身份 header | 成为事实源或替 Engine 最终授权 |
| Engine Host | 独立验 token，查 Tenant/Membership Active，构造唯一上下文 | 信任裸身份 header |
| Engine | 配置/注册表/热更新使用租户复合键，在请求上下文内解析运行配置 | 拥有 Tenant/Membership 或缓存无租户配置 |
| Core | 授权 TenantResourceKey、owner/share 和能力绑定 | 依赖 HTTP、Redis key 字符串或默认租户 |
| Infrastructure | 显式 tenant filter、复合键/FK、审计、事务与可选 RLS | 用当前用户隐式改变 repository 语义 |
| Redis | 可重建投影、缓存、锁、索引与版本通知 | 充当 Tenant/Membership 或业务事实源 |
| PostgreSQL | Tenant、Membership、Policy、业务元数据和 AuditEvent 事实源 | 只依赖应用先过滤 |
| 对象存储 | tenant partition、加密、生命周期与删除标记 | 根据 object key 单独授权 |
| 前端 | JWT 模式只显示 `/me` 租户；切换租户取得新 token | 把 localStorage TenantId 当生产身份 |

## 键与存储

TenantId 按 `Ordinal` 比较，进入 Redis 使用完整 SHA-256 `tenantHash`；资源 ID 用 base64url 或哈希编码。哈希减少标识暴露，但不构成授权。Redis Cluster 使用 `{t:<tenantHash>}` hash tag：

| 用途 | Redis v2 格式 |
|------|---------------|
| Agent/能力配置与索引 | `openagent:v2:{t:<tenantHash>}:<type>:<resourceKey>` / `...:<type>:index` |
| 会话/锁/Provider 亲和 | `openagent:v2:{t:<tenantHash>}:conversation:<id>` / `...:lock:<id>` / `...:provider:<id>` |
| 幂等/查询缓存 | `openagent:v2:{t:<tenantHash>}:subject:<subjectHash>:idempotency|query:<digest>` |
| 限流/访问投影 | `openagent:v2:{t:<tenantHash>}:ratelimit:<policy>:<subjectHash>` / `...:tenant-access:<subjectHash>` |
| Engine 注册 | `openagent:v2:global:engine:registry:<engineId>` |

热更新通知携带 TenantId/hash、ResourceType、ResourceId、Version 和操作，不携带配置或 secret；subscriber 以复合键刷新并丢弃旧版本。v1 全局键迁移期只兼容读取，不能产生新的无租户资源。

PostgreSQL 新增 `tenants`、`tenant_memberships`、`tenant_policies`、`audit_events`；所有租户表 `tenant_id NOT NULL`，主/唯一键和子表 FK 包含 TenantId。查询仍显式 tenant predicate，RLS 仅作第二道防线。无法证明归属的旧数据进入 quarantine，不自动归入 `default`。

对象键建议为用户 `files/tenants/<tenantHash>/users/<subjectHash>/assets/...`、Skill `.../shared/skills/...`、Channel `.../channels/<bindingHash>/...`、平台模板 `files/platform/templates/...`。数据库归属和引用是授权事实，读写还要复核对象 partition 与内容哈希。

## 审计

可变租户资源至少保存 `TenantId`、创建/更新/删除时间与 actor、`Version`；secret 另存 `SecretVersion`、`RotatedAt`，不存明文。不可变 AuditEvent 保存 scope、actor `(iss,sub)`、角色/AccessMode、action、resource key、outcome、policy version、TraceId、来源服务和管理员理由；不得保存 token、API key、完整 prompt、正文或工具结果。

TenantId 可用于请求日志排障，但不能成为低基数指标标签。

## 未来 Channel 边界

Channel Adapter 先验证外部平台签名，再用 service principal M2M JWT 调 Router。服务端 `ChannelBinding(provider, externalTenantId, installationId)` 映射唯一 TenantId；payload/header 不能选择租户。Router/Engine 据此赋值 `AccessMode=Channel`、ConversationType 和 ClientType；会话键含 `(TenantId, ChannelBindingId, ExternalThreadId)`，文件用 Channel scope。

跨组织会议仍归属安装 Agent 的单一租户，外部参与者不产生跨租户共享。当前客户端可传 `conversationType/clientType`，且 `InternalServiceOptions` 尚未接入认证链；Channel 上线前必须先封住这两个入口。
