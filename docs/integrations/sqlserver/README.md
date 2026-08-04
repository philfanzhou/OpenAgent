# SQL Server Integration

Agent.Core 使用 SQL Server 作为会话冷归档存储，提供长期持久化能力。通过 `DualWriteConversationStore` 实现双写（Redis 热存储 + SQL Server 冷归档）。

## Core Capabilities
| Capability | Description |
|-----------|-------------|
| 冷归档 | 会话记录归档 + 消息行级归档 |
| 自动建表 | `ConversationRecords` + `ConversationMessages` + TVP 类型 + 索引 |
| Upsert | `MERGE INTO`（会话记录）|
| 批量写入 | TVP（Table-Valued Parameter）批量消息插入 |
| 重试 | 指数退避重试 |
| 双写 | 通过 `DualWriteConversationStore` 异步 Fire-and-Forget |

## Architecture
```text
DualWriteConversationStore : IConversationStore
  ├── IConversationStore (RedisConversationStore) — 热存储（主）
  └── IConversationRepository (SqlServerConversationRepository) — 冷归档（从）
```
- 读取：仅从热存储
- 写入：先写热存储，成功后异步写冷归档
- 冷归档失败不影响主流程

## Current Status
**Partial** — 核心归档能力已实现。以下能力尚未实现（规划中，共 6 项）：软删除、会话标题生成、`ConversationMessagesArchive` 归档表、数据分层迁移、审计端点、审计跨表查询。

## Limits
- 无冷归档补偿机制
- 大批量消息归档有优化空间
- 部分字段（Title、IsDeletedByUser、DeletedAt、ArchivedAt）已在表结构中，但对应功能未完全实现

## Source
- Implementation: `Backend/src/OpenAgent.Core/Conversation/Repository/SqlServerConversationRepository.cs`
- Store: `Backend/src/OpenAgent.Core/Conversation/Store/DualWriteConversationStore.cs`
- Contracts: `Backend/src/OpenAgent.Contracts/Conversation/ConversationStoreOptions.cs`
- Tests: `Backend/tests/OpenAgent.Core.Tests/Conversation/`
