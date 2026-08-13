# Database

PostgreSQL 是当前 OpenAgent 持久化业务数据的唯一事实源；EF Core migration 位于
`Backend/src/OpenAgent.Infrastructure/Persistence/Migrations/`。`IConversationStore` 与文件资产契约不绑定
特定数据库，后续 Provider 可在 Infrastructure 层独立实现。应用进程不自动建表，部署流水线应显式执行 migration。

## 表清单

| 表 | 说明 |
|---|---|
| `openagent.conversations` | 会话头、所有者、状态与乐观并发版本 |
| `openagent.conversation_messages` | 独立的有序会话消息，元数据使用 `jsonb` |
| `openagent.file_assets` | 按租户、用户和会话归属的文件资产元数据与对象键 |
| `openagent.conversation_file_references` | 文件在会话中的引用 |
| `openagent.message_file_references` | 文件在具体消息中的引用，用于预览和治理 |

文件字节保存在 S3/MinIO；对象键使用租户、用户和会话的不可逆哈希分区，但授权与生命周期事实仍只由 PostgreSQL 拥有。Redis 如被部署，保存
可过期的会话热副本并提供分布式会话锁；它不拥有会话或文件资产事实，所有热副本均可由数据库回填。

## 关系

```text
Conversation 1 --- * ConversationMessage
Conversation * --- * FileAsset (conversation_file_references)
ConversationMessage * --- * FileAsset (message_file_references)
```

详细字段见 [ConversationRecords](./tables/ConversationRecords.md) 和 [FileAssets](./tables/FileAssets.md)。
