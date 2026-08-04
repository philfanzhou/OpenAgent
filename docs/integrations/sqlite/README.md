# SQLite Integration

Agent.Core 使用 SQLite 作为会话冷归档存储，通过 `DualWriteConversationStore` 实现双写（Redis 热存储 + SQLite 冷归档）。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 冷归档 | 会话记录归档 + 消息行级归档 |
| 自动建表 | `ConversationRecords` + `ConversationMessages` 表与索引 |
| Upsert | `INSERT ON CONFLICT DO UPDATE`（会话记录）|
| 批量写入 | 事务逐行 `INSERT OR IGNORE`（消息行）|
| 重试 | 指数退避重试（`ColdArchiveRetryCount` / `ColdArchiveRetryDelayMs`）|
| 双写 | 通过 `DualWriteConversationStore` 异步 Fire-and-Forget |

## Architecture
```text
DualWriteConversationStore : IConversationStore
  ├── IConversationStore (RedisConversationStore) — 热存储（主）
  └── IConversationRepository (SqliteConversationRepository) — 冷归档（从）
```
- 读取：仅从热存储
- 写入：先写热存储，成功后异步写冷归档
- 冷归档失败不影响主流程

## Current Status
**Implemented** — 适用于开发/测试环境和单机部署场景。

## Limits
- 日期时间以 ISO 8601 字符串存储（SQLite 不原生支持 DateTimeOffset）
- 无冷归档补偿机制（自动重试失败的归档）
- 无软删除、会话标题生成等（部分在 SQL Server 实现中规划）

## Source
- Implementation: `Backend/src/OpenAgent.Core/Conversation/Repository/SqliteConversationRepository.cs`
- Store: `Backend/src/OpenAgent.Core/Conversation/Store/DualWriteConversationStore.cs`
- Contracts: `Backend/src/OpenAgent.Contracts/Conversation/ConversationStoreOptions.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Conversation/`
