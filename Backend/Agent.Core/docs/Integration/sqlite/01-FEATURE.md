# SQLite — 功能概述

## 核心能力

Agent.Core 使用 SQLite 作为会话冷归档存储，提供轻量级持久化能力。通过 `DualWriteConversationStore` 实现双写（Redis 热存储 + SQLite 冷归档）。

## 关键接口与类

| 接口/类 | 所在文件 | 职责 |
|---------|----------|------|
| `SqliteConversationRepository` | `src/Core/Conversation/Repository/SqliteConversationRepository.cs` | SQLite 会话归档实现 |
| `DualWriteConversationStore` | `src/Core/Conversation/Store/DualWriteConversationStore.cs` | 双写存储（Redis + 冷归档） |
| `ConversationStoreOptions` | `Agent.Contracts/Conversation/ConversationStoreOptions.cs` | 存储配置选项（ColdArchiveProvider=Sqlite） |

## 功能范围

- 会话记录冷归档（`ArchiveAsync`）+ 消息行级归档（`ArchiveMessagesAsync`）
- 自动建表（`ConversationRecords` + `ConversationMessages`）
- INSERT ON CONFLICT 实现 Upsert（幂等写入）
- 事务逐行 INSERT OR IGNORE 批量消息写入
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

## 适用场景

- 开发/测试环境（无需 SQL Server）
- 单机部署（嵌入式数据库）
- 轻量级持久化需求
