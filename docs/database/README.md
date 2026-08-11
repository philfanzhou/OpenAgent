# Database — Agent.Core

> 本目录是数据库结构的唯一事实源。

## 表清单

| 表名 | 说明 | 详细文档 |
|------|------|----------|
| ConversationRecords | 会话主记录（含消息 JSON） | [tables/ConversationRecords.md](./tables/ConversationRecords.md) |
| FileAssets | 用户文件元数据与对象存储定位 | [tables/FileAssets.md](./tables/FileAssets.md) |

> 注意：ConversationMessage 不是独立表，而是以 JSON 数组形式存储在 ConversationRecords.MessagesJson 列中。

## 存储架构

Agent.Core 使用**双写架构**：

- **热存储**：Redis（String 类型，key 格式 `conversation:{tenantId}:{conversationId}`）
- **冷归档**：SQL Server（ConversationRecords 表）

写入路径：Service → DualWriteConversationStore → Redis（同步）+ SQL Server（异步补偿）

## 实体关系

```
ConversationRecord 1──* ConversationMessage (嵌入在 MessagesJson 中)
ConversationRecord *──* FileAssets (通过 ConversationFileReferences)
```

## 迁移历史

当前使用代码自动建表（`IConversationRepository.EnsureInitializedAsync`，由 `SqlServerConversationRepository` / `SqliteConversationRepository` 实现），无 EF Core Migration 文件。

## 已移除的表

无。
