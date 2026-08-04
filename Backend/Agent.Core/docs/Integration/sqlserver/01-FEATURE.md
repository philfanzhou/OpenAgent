# SQL Server — 功能概述

## 核心能力

Agent.Core 使用 SQL Server 作为会话冷归档存储，提供长期持久化能力。通过 `DualWriteConversationStore` 实现双写（Redis 热存储 + SQL Server 冷归档）。

## 关键接口与类

| 接口/类 | 所在文件 | 职责 |
|---------|----------|------|
| `SqlServerConversationRepository` | `src/Core/Conversation/Repository/SqlServerConversationRepository.cs` | SQL Server 会话归档实现 |
| `DualWriteConversationStore` | `src/Core/Conversation/Store/DualWriteConversationStore.cs` | 双写存储（Redis + SQL Server） |
| `ConversationStoreOptions` | `Agent.Contracts/Conversation/ConversationStoreOptions.cs` | 存储配置选项 |

## 功能范围

- 会话记录冷归档（`ArchiveAsync`）+ 消息行级归档（`ArchiveMessagesAsync`）
- 自动建表（`ConversationRecords` + `ConversationMessages`）
- MERGE INTO 实现 Upsert（幂等写入）
- TVP（Table-Valued Parameter）批量消息插入
- 指数退避重试
- 通过 `DualWriteConversationStore` 实现双写
- 归档为异步 Fire-and-Forget，不阻塞主流程

## DualWrite 架构

```
DualWriteConversationStore : IConversationStore
  ├── IConversationStore (RedisConversationStore) — 热存储（主）
  └── IConversationRepository — 冷归档（从）
```

- 读取：仅从热存储读取
- 写入：先写热存储，成功后异步写冷归档
- 冷归档失败不影响主流程
