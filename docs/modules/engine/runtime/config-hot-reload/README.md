# Config Hot Reload

ConfigHotReload 通过 Redis Pub/Sub 订阅配置变更通知，实时更新内存中的 ConfigSnapshot，无需重启服务即可生效。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| Pub/Sub 订阅 | 监听 6 个频道（1 个当前 + 5 个遗留）|
| 结构化消息 | ConfigUpdate / IncrementalUpdate 统一从 Redis 全量刷新；FullSync 清空快照 |
| TTL 自愈 | 快照条目按 `AbsoluteExpirationMinutes`（默认 5 分钟）绝对过期 |
| 遗留兼容 | 非 JSON 消息在 `agent:config:changed` 频道视为 agentId 触发全量刷新 |

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
                     ▼                ▼
          LegacyMessageHandler  FullConfigRefresher ──→ ConfigSnapshot (IMemoryCache + TTL)
```

## Current Status
**Implemented** — 不再按 ConfigType 选择 handler，不再 patch，不再维护版本。所有结构化消息统一全量刷新。

## Limits
- 发布环境需验证真实 Redis Pub/Sub 的断线重连和订阅恢复
- 自动化测试不模拟真实 Redis Pub/Sub 的完整生命周期

## Source
- Core: `src/Engine/Reload/`（HotReloadService, ConfigUpdateDispatcher, LegacyMessageHandler, FullConfigRefresher）
- Snapshot: `src/Engine/Models/ConfigSnapshot.cs`, `Config/ConfigSnapshotOptions.cs`
- Tests: `test/OpenAgent.Engine.Tests/Config/HotReloadTests.cs`, `ConfigUpdateRegistrationTests.cs`
