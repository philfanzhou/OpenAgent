# Config Hot Reload

ConfigHotReload 通过 Redis Pub/Sub 订阅配置变更通知，实时更新 Agent 配置快照和 LLM 注册表，无需重启服务即可生效。一次执行只使用开始时解析的配置，热更新影响后续请求。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| Pub/Sub 订阅 | 监听 `agent:config:updates` 统一频道和 5 个遗留频道 |
| 结构化消息 | 按 `resourceType`、`resourceId`、`operation` 区分 Agent 与 LLM 变更 |
| Redis 写入一致性 | 默认模式的 Redis 事实数据和通知在同一事务提交；写入节点在返回前同步应用通知 |
| PostgreSQL 验证 | 数据库先提交，再原子刷新独立 Redis 缓存并发布 `PostgreSqlAgent` 事件 |
| Agent 重载 | 收到事件后从 Redis 全量刷新 Agent 快照；FullSync 清空全部快照 |
| LLM 重载 | 收到更新事件后从 Redis 重载 Provider，删除事件移除本地注册项 |
| TTL 自愈 | 快照条目按 `AbsoluteExpirationMinutes`（默认 5 分钟）绝对过期 |
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
**Implemented** — 默认 Redis 链路保持不变。PostgreSQL 验证模式在数据库提交后更新本节点快照，再尽力刷新
Redis 派生缓存和发布结构化事件；Redis 失败不回滚数据库，其他节点最迟在内存/Redis TTL 到期后回源数据库。

## Limits
- Redis Pub/Sub 是瞬时通知；断线期间未收到的事件由 Agent 快照 TTL 提供最终自愈
- PostgreSQL 与 Redis 没有原子提交；正式迁移前需要 outbox/重放器和可观测的缓存延迟指标
- 自动化单元测试覆盖事件分发和重载，不模拟真实 Redis Pub/Sub 的完整生命周期

## Source
- Core: `Backend/src/OpenAgent.Engine/Reload/`（HotReloadService, ConfigUpdateDispatcher, FullConfigRefresher, LlmProfileRefresher）
- Snapshot: `Backend/src/OpenAgent.Engine/Models/ConfigSnapshot.cs`, `Backend/src/OpenAgent.Engine/Config/ConfigSnapshotOptions.cs`
- Tests: `Backend/tests/OpenAgent.Engine.Tests/Config/HotReloadTests.cs`, `Backend/tests/OpenAgent.Engine.Tests/Config/ConfigUpdateRegistrationTests.cs`
