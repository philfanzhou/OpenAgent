# Database

PostgreSQL 是当前 OpenAgent 持久化业务数据的唯一事实源；EF Core migration 位于
`Backend/src/OpenAgent.Framework/Persistence/Migrations/`。`IConversationStore` 与文件资产契约不绑定
特定数据库，后续 Provider 可在 Framework 层独立实现。生产部署流水线应显式执行 migration；Development 可配置启动时应用 migration。迁移历史固定在 `public.__EFMigrationsHistory`，避免与 PostgreSQL 用户同名 schema 影响重启。

## 表清单

| 表 | 说明 |
|---|---|
| `openagent.conversations` | 会话头、所有者、状态与乐观并发版本 |
| `openagent.conversation_messages` | 独立的有序会话消息，元数据使用 `jsonb` |
| `openagent.file_assets` | 用户文件资产元数据与对象键 |
| `openagent.file_share_links` | 文件分享链接（令牌哈希主键、过期与下载计数），详见 [FileShareLinks](./tables/FileShareLinks.md) |
| `openagent.conversation_file_references` | 文件在会话中的引用 |
| `openagent.message_file_references` | 文件在具体消息中的引用，用于预览和治理 |
| `openagent.agent_configurations` | Agent 基础字段、嵌套能力配置与乐观并发版本；主键 `(TenantId, AgentId)` |
| `openagent.llm_configurations` | LLM 模型连接、ContextTokens、Modality 和服务端加密 Key；主键 `(TenantId, ProfileId)` |
| `openagent.runtime_configurations` | 独立 Runtime 设置，主键 `(TenantId, RuntimeId)`，JSON 保存名称、类型与单轮时限，不包含资源绑定 |
| `openagent.mcp_configurations` | 持久化 MCP 定义，主键 `(TenantId, Name)` |
| `openagent.llm_interaction_logs` | 大模型交互请求/响应审计日志（已脱敏），按会话与轮次 TraceId 检索 |

文件字节生产环境保存在 S3/MinIO，Development 可持久化到本机受限目录；对象存储不保存租户、用户、会话或生命周期事实。Redis 如被部署，保存
可过期的会话热副本、派生配置缓存并提供分布式锁与 Pub/Sub；这些派生数据均可由数据库回填。
Agent 配置以 PostgreSQL 为唯一事实源；Redis 仅保存带 TTL 的租户派生缓存。migration 不会自动导入历史
`agent:config:*` Redis 数据，存量部署必须在切流前单独完成数据迁移。Agent 的 RAG/内嵌 OpenCode 模型凭据及独立 LLM Key 均由服务端加密保存，管理 API 返回时脱敏。

## 关系

```text
Conversation 1 --- * ConversationMessage
Conversation * --- * FileAsset (conversation_file_references)
ConversationMessage * --- * FileAsset (message_file_references)
FileAsset 1 --- * FileShareLink
```

详细字段见 [ConversationRecords](./tables/ConversationRecords.md)、[LlmInteractionLogs](./tables/LlmInteractionLogs.md)、[FileAssets](./tables/FileAssets.md) 和 [Configurations](./tables/Configurations.md)。
