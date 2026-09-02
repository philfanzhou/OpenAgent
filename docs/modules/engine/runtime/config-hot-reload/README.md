# Config Hot Reload

ConfigHotReload 通过 Redis Pub/Sub 订阅配置变更通知，实时更新 Agent 配置快照和 LLM 注册表，无需重启服务即可生效。一次执行只使用开始时解析的配置，热更新影响后续请求。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| Pub/Sub 订阅 | 监听 `agent:config:updates` 统一频道和 5 个遗留频道 |
| 结构化消息 | 按 `resourceType`、`resourceId`、`operation` 区分 Agent 与 LLM 变更 |
| PostgreSQL 事实提交 | 数据库先提交，再刷新租户 Redis 缓存并发布 `PostgreSqlAgent` 事件 |
| 租户事件 | Agent 事件携带 tenantId，接收端只刷新对应租户与 Agent 的快照 |
| Agent 重载 | 收到事件后从 Redis/数据库刷新租户 Agent 快照；FullSync 清空全部快照 |
| LLM 重载 | 收到更新事件后从 Redis 重载 Provider，删除事件移除本地注册项 |
| TTL 与协调自愈 | Redis 缓存默认 300 秒过期，后台默认每 60 秒按数据库事实重新投影 |
| 遗留兼容 | 继续处理 `agent:config:changed` 和 `llm:registry:changed` 的纯 ID 消息 |

## Architecture
```text
Config Management ── Publish ──> Redis Pub/Sub
                                      │
                                      ▼
                              HotReloadService（订阅、异常边界）
                                      │ ProcessMessage
                                      ▼
                           ConfigUpdateDispatcher
                     ┌────────────────┼────────────────┐
                     ▼                ▼                ▼
          LegacyMessageHandler  FullConfigRefresher  LlmProfileRefresher
                                      │                │
                                      ▼                ▼
                              ConfigSnapshot       ILlmRegistry
```

## Current Status
**Implemented** — PostgreSQL 提交后更新本节点租户快照，再尽力刷新 Redis 派生缓存并发布结构化事件。
Redis 失败不回滚数据库；其他节点通过缓存未命中回源和周期协调恢复到数据库版本。

## Limits
- Redis Pub/Sub 是瞬时通知；断线期间未收到的事件由 TTL、数据库回源和周期协调最终自愈
- PostgreSQL 与 Redis 没有原子提交；当前保证最终一致，不保证审计级事件投递。需要严格交付时应增加 outbox/重放器
- 自动化单元测试覆盖事件分发和重载，不模拟真实 Redis Pub/Sub 的完整生命周期

## Source
- Core: `Backend/src/OpenAgent.Engine/Reload/`（HotReloadService, ConfigUpdateDispatcher, FullConfigRefresher, LlmProfileRefresher）
- Snapshot: `Backend/src/OpenAgent.Engine/Models/ConfigSnapshot.cs`, `Backend/src/OpenAgent.Engine/Config/ConfigSnapshotOptions.cs`
- Tests: `Backend/tests/OpenAgent.Engine.Tests/Config/HotReloadTests.cs`, `Backend/tests/OpenAgent.Engine.Tests/Config/ConfigUpdateRegistrationTests.cs`
