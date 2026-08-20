# 租户概念模型

> 状态：Proposed
>
> 调研基线：`origin/main@fbeccb263c2ca8ba080c5015ecb8ab2679e2e15b`（2026-08-20）

租户（Tenant）是客户组织在 OpenAgent 中的**安全、数据、配置、配额和审计边界**。租户不是用户、IdP、用户组、部署实例或字符串命名空间；同一用户可加入多个租户，但一次交互只能有一个有效租户上下文。

## 核心实体

| 实体 | 标识与关键字段 | 约束 |
|------|----------------|------|
| `Tenant` | `TenantId`、`DisplayName`、`Status`、`StatusVersion`、审计字段 | ID 不可变、按 `Ordinal` 比较、删除后不复用 |
| `TenantMembership` | `(TenantId, Issuer, SubjectId)`、`PrincipalType`、`Roles`、`Status`、有效期 | 同一 subject 在同一租户只有一个有效成员关系 |
| `TenantPolicy` | `(TenantId, PolicyVersion)`、配额、保留策略、集成与管理员数据访问策略 | 版本化；执行请求固定读取到的版本 |

TenantId 是 1–128 字符的不透明值，禁止首尾空白和控制字符；不做大小写归一化，进入日志/键前按各边界安全编码。身份以 JWT 的 `(iss, sub)` 唯一标识，展示用 `UserId` 不能作为跨 IdP 的主键。`PrincipalType` 至少区分 `User` 与 `Service`；Channel 外部参与者通过 Channel 绑定映射，不伪造成平台用户。

## 生命周期

| Tenant 状态 | 允许行为 |
|-------------|----------|
| `Provisioning` | 仅平台控制面创建配置和首个 Owner |
| `Active` | 按成员角色访问租户资源 |
| `Suspended` | 普通成员/服务拒绝；平台管理员仅可恢复、导出和审计 |
| `Deleting` | 禁止新执行和配置写入；允许受控导出、保留和清理任务 |
| `Deleted` | 只保留 tombstone 与法定审计，不可恢复 |

合法迁移为 `Provisioning -> Active <-> Suspended -> Deleting -> Deleted`。进入 `Deleting` 前必须处理活动任务、Channel 绑定、对象清理和 secret 吊销。

成员状态为 `Invited -> Active <-> Suspended -> Removed`；只有 `Active` 成员能进入正常数据面，且最后一个 Owner 不得被移除。

| 租户角色 | 典型权限 | 限制 |
|----------|----------|------|
| `Owner` | 生命周期、Owner 交接、成员、策略和共享资源管理 | 不自动读取用户私有正文 |
| `Admin` | 成员与 Agent/Skill/LLM/MCP/RAG 管理、审计元数据 | 不能删除租户或越过私有内容策略 |
| `Builder` | 创建/维护 Agent 和能力配置并测试 | 不管理成员和生命周期 |
| `Member` | 使用 Agent，管理自己的 Conversation/FileAsset | 不修改共享配置 |
| `Auditor` | 读取配置元数据、变更和审计事件 | 不读取 secret、执行 Agent 或读取私有正文 |

角色可组合，实际权限取成员角色与 JWT scope 的交集。`PlatformAdmin` 是平台角色，不属于成员关系，也没有隐式数据面通行权。

## 身份来源规则

| 环境/模式 | TenantId 来源 | `X-Tenant-Id` 行为 |
|-----------|---------------|--------------------|
| Production / JWT Bearer | 已验证 token 中唯一非空的 `tenant_id` 或 `tid` | 不能建立身份；claim 缺失仍返回 403，冲突返回 403，Router 转发前移除 |
| Development / JWT Bearer | 与 Production 相同 | 不提供 header 回退 |
| Development / Basic | Basic claim；claim 缺失时可回退 header | 仅此组合兼容；冲突返回 403，格式错误返回 400 |

`tenant_id` 与 `tid` 同时出现时必须完全相同。生产多租户切换通过 IdP 登录或 token exchange 取得目标租户 token，前端不能用 header 选择租户。Development 回退在共享 Authentication 边界完成，Router 与 Engine 直连复用同一解析器。

## 单一请求安全上下文

| 部分 | 必要字段 |
|------|----------|
| `AuthenticatedSubject` | `Issuer`、`SubjectId`、`UserId`、认证方式、JWT roles/scopes、audience |
| `TenantContext` | `TenantId`、`TenantStatus`、`StatusVersion`、`MembershipStatus`、`TenantRoles`、`Source`、`AccessMode` |
| `RequestContext` | `TraceId`、`ClientType`、调用方类型、可选 Channel 绑定键 |

`AgentUserContext` 演进为上述事实的只读投影，不再由 Router Items、Engine feature 和 `ICurrentUserContext` 分别解析。`TenantContext.Source` 只允许 `JwtClaim`、`DevelopmentHeader`、`ServiceBinding`；生产浏览器请求只能是 `JwtClaim`。

```text
Browser/API -> Router 验证 token/租户投影并租户化路由键
            -> 转发 Bearer token，移除客户端身份 header
            -> Engine Host 再验 token，并查 PostgreSQL 权威租户/成员状态
            -> Core 授权 TenantResourceKey 与 owner/share policy
            -> Infrastructure 执行 tenant-qualified 存储操作
```

Router 的 Redis 租户投影只用于提前拒绝，不生成身份；Engine Host 是权威状态复核点。Core 不读取 `HttpContext`、header 或 `default` tenant。

## 响应语义

| 场景 | 结果 |
|------|------|
| token 缺失、无效或受众错误 | `401` |
| 已认证但 tenant claim 缺失/冲突，或租户/成员非 Active | `403` |
| 同租户操作权限不足 | `403` |
| 资源不存在或普通调用跨租户 | 统一 `404`，避免确认其他租户资源存在 |
| Development header 格式错误 | `400` |

详细资源、组件和存储边界见 [BOUNDARIES.md](./BOUNDARIES.md)，现状证据见 [CURRENT-STATE.md](./CURRENT-STATE.md)，实施顺序见 [MIGRATION.md](./MIGRATION.md)。
