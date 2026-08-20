# 租户架构迁移建议

> 依赖：[概念模型](./DESIGN.md) 与 [资源边界](./BOUNDARIES.md)。先封住身份/资源入口，再迁移键和数据；每阶段必须可独立发布和回滚。

## 阶段与出口条件

### Phase 0：基线防回归

建立 Tenant A/B 使用相同资源 ID 的隔离矩阵；覆盖 Production claim/header 与 Development Basic header；清点仅 ID API、`default` fallback 和全局 Redis key。出口：每条链路有 owner、当前 key、事实源和迁移负责人。

### Phase 1：身份与上下文

Hosting 统一 resolver；Production/JWT 只认 claim，Development/Basic 才回退 header；Router/Engine 复用解析结构但 Engine 独立验 JWT；合并重复用户上下文；前端 JWT 模式隐藏租户输入。出口：header 无法改变生产 TenantId，直连与 Router 规则一致。

### Phase 2：Tenant、成员与角色

新增 Tenant/Membership/Policy/Audit schema 和显式 development seed；Engine 查 PostgreSQL 权威状态，Router 读版本化 Redis 投影；实施状态机、Owner 保护和角色/scope 交集。出口：Suspended/Deleting/Removed 在所有入口 fail closed。

### Phase 3：ResourceKey 与 Redis v2

引入 `TenantResourceKey` 和 key factory；先迁 Agent/LLM/MCP/RAG/ACL/snapshot/hot reload；内存 registry、索引和通知使用复合键。滚动发布采用“v2 写 + v2/v1 读”，未知归属不进 `default`。出口：不同租户可复用相同 ID，新写入只有 v2 键。

### Phase 4：PostgreSQL 复合边界

给 Message 和 Conversation/File/Message reference 补 TenantId；切换 tenant-qualified unique/FK 和 repository；加入审计字段、AuditEvent 和可选 RLS；未知旧数据进 quarantine。出口：数据库可独立拒绝跨租户引用。

### Phase 5：资源与管理面

Agent/Skill/LLM/MCP/RAG 管理 API 删除未限定 tenant 的重载；实施 OwnerScope、同租户能力绑定、tenant secret 加密；PlatformAdmin 使用独立路径、理由和审计。出口：普通 Admin 无法枚举其他租户，平台操作可追溯。

### Phase 6：Conversation、FileAsset 与对象

显式化 User/Internal/Channel owner；Infrastructure 不再隐式读取当前用户；FileAsset 加 User/Conversation/Channel/Tenant scope；对象迁到版本化分区并定义租户删除/tombstone。出口：私有正文不因 Admin 自动暴露，跨租户引用有三层阻断。

### Phase 7：RAG 与 Channel

每个 RAG adapter 声明 tenant filter 能力，不支持者 fail closed；RagInstance 成为租户资源并移除空 ACL/`default`；实现 ChannelBinding、M2M 身份和服务端 ConversationType/ClientType。出口：真实出站检索含可验证 filter，客户端不能伪造 Channel 租户。

## 后续 PR 拆分

| PR | 范围 | 依赖 |
|----|------|------|
| 1 | Authentication resolver、claim/header、前端 Development 行为与契约测试 | 无 |
| 2 | Tenant/Membership/Policy/Audit、状态机、角色、access evaluator | PR 1 |
| 3 | ResourceKey、Redis v2 key factory、通知 envelope、双读写框架 | PR 2 |
| 4 | Agent/LLM/MCP/RAG/ACL/registry/snapshot 复合键 | PR 3 |
| 5 | Conversation/FileAsset TenantId、复合 FK、repository 与回填 | PR 2、3 |
| 6 | OwnerScope、管理 API、PlatformAdmin 控制面与审计 | PR 4、5 |
| 7 | RAG adapter capability/fail-closed 和外部集成验证 | PR 4、6 |
| 8 | ChannelBinding、M2M、Channel Conversation/File | PR 2、5、6 |
| 9 | 移除 v1 key、仅 ID API、`default` fallback 和迁移开关 | 全部验收后 |

## 兼容与发布

- JWT：先统计缺 tenant claim 的 token 并完成 IdP mapping；Production 不设 header 兼容期。
- Redis：feature flag 控制双写、优先读、只读；通知带 tenant+version，监控 v1 命中并对账内容哈希。
- PostgreSQL：先加 nullable 列并分批回填，再加 NOT NULL/复合约束，最后切 repository。
- API：先加 tenant-qualified 内部接口再迁调用者；外部 URL 不必暴露 TenantId，仍从上下文注入。
- 对象：复制新 key、校验哈希、更新数据库、延迟删旧对象；所有清理幂等。

## 主要风险

| 风险 | 缓解 |
|------|------|
| 旧全局 ID 冲突 | 生成归属报告和映射表，人工确认，不自动覆盖 |
| JWT 缺 claim 导致集中 403 | 提前观测、IdP 验收、非生产演练，不用 Production header 降级 |
| 双写/热更新乱序 | tenant+version 通知、丢弃旧版本、v1/v2 对账 |
| Router 投影不可用 | 管理面 fail closed；数据面仅按显式策略交 Engine 权威复核并告警 |
| 回填锁表或对象迁移中断 | 分批/可暂停迁移、状态机、哈希校验和延迟删除 |
| Admin 扩大隐私面 | 独立 ComplianceReader、step-up、理由、短时授权、不可变审计 |
| RAG 忽略 filter | adapter capability 与真实出站测试；无法证明时 fail closed |
| 前端本地租户过期 | JWT 模式只显示 `/me`，租户变化重新认证并清空旧工作区 |

## 验收与完成定义

验收必须覆盖：相同资源 ID 的 A/B 隔离；claim/header/状态/角色矩阵；Conversation/File owner 与复合 FK；Redis 混合版本和失效；对象分区和 secret redaction；每个 RAG adapter 的真实出站 filter；JWT 与 Development Basic 前端行为；Channel 绑定防伪造。

只有当 v1 全局键、仅 ID API 和租户资源 `default` fallback 已移除，所有资源统一使用 TenantContext/ResourceKey，数据库、对象存储、RAG 和 Channel 均通过端到端隔离测试后，设计才能标记为 Implemented。
