# Database

PostgreSQL 是 OpenAgent 持久化业务数据的唯一事实源；EF Core migration 位于 `Backend/src/OpenAgent.Persistence/Migrations/`。应用进程不自动建表，部署流水线应显式执行 migration。

## 表清单

| 表 | 说明 |
|---|---|
| `openagent.conversations` | 会话头、所有者、状态与乐观并发版本 |
| `openagent.conversation_messages` | 独立的有序会话消息，元数据使用 `jsonb` |
| `openagent.file_assets` | 用户文件资产元数据与对象键 |
| `openagent.conversation_file_references` | 文件在会话中的引用 |
| `openagent.message_file_references` | 文件在具体消息中的引用，用于预览和治理 |

文件字节保存在 S3/MinIO；对象存储不保存租户、用户、会话或生命周期事实。Redis 如被部署，只能承担短生命周期协调功能，不能保存会话或文件资产。

## 关系

```text
Conversation 1 --- * ConversationMessage
Conversation * --- * FileAsset (conversation_file_references)
ConversationMessage * --- * FileAsset (message_file_references)
```

详细字段见 [ConversationRecords](./tables/ConversationRecords.md) 和 [FileAssets](./tables/FileAssets.md)。
