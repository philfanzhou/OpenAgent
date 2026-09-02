# Redis 集成（Engine）

Redis 用于可重建缓存、服务注册/心跳、分布式锁，以及尚未迁移的 MCP/RAG 目录。Redis 不拥有 Agent 或 LLM 配置事实。

| Key | 用途 | 生命周期 |
|---|---|---|
| `agent:config-cache:{tenantId}:{agentId}` | PostgreSQL Agent 派生缓存 | 默认 300 秒 |
| `agent:config-cache:index:{tenantId}` | Agent 缓存诊断索引 | 可重建 |
| `llm:config-cache:{tenantId}:{profileId}` | PostgreSQL LLM 派生缓存，含服务端 API Key | 默认 300 秒 |
| `engine:registry:{engineId}` | Engine 服务发现 | 心跳 TTL |
| `openagent:conversation-lock:{tenantId}:{conversationId}` | 会话分布式锁 | 短 TTL |

配置更新流程是 PostgreSQL 提交后立即覆盖对应缓存，不使用 Pub/Sub 或进程内 Snapshot。Redis 故障不会回滚数据库提交；读取自动回源 PostgreSQL。
