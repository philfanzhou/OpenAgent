# Data Ownership — OpenAgent

## 本服务拥有的数据实体

| 实体 | 存储位置 | 说明 |
|------|----------|------|
| ConversationRecord | PostgreSQL | 会话主记录、状态和乐观并发版本 |
| ConversationMessage | PostgreSQL | 独立有序消息和消息级文件引用 |
| FileAsset | PostgreSQL | 用户文件元数据、归属、状态与会话引用 |
| File Object | S3 兼容对象存储 | 文件原始字节；不保存授权和会话事实 |
| AgentConfiguration | PostgreSQL | 按 `(TenantId, AgentId)` 保存基础字段、嵌套能力配置和乐观并发版本，是 Agent 配置唯一事实源 |
| LlmConfiguration | PostgreSQL | 按 `(TenantId, ProfileId)` 保存模型、上下文窗口、连接参数与租户绑定的加密 API Key，是 LLM 配置唯一事实源 |

## 引用的外部数据（只读）

| 数据 | 来源 | 用途 |
|------|------|------|
| McpServerConfig | Redis `mcp:published:index` + IMcpRegistry (内存注册) | 获取独立 MCP Server 连接配置；不复制到 Agent |
| RagInstanceConfig | IRagRegistry (内存注册) | 获取 RAG 实例配置 |
| Agent Skill 文件目录 | S3 兼容对象存储 | ZIP/MD 上传后按解压目录写入文件对象；运行时 materialize 到请求级临时目录并由 MAF `AgentSkillsProvider` 读取 |
| Skill 目录元数据 | PostgreSQL `SkillDefinitions`；Redis 为派生缓存 | 按租户保存可绑定的 Skill 元数据；不表示某个 Agent 已绑定 |

## 持久化规则

- ConversationRecord、ConversationMessage 与 FileAsset 只写入 PostgreSQL。
- 文件字节只写入 S3 兼容对象存储；对象存储不拥有用户、租户或会话事实。
- Skill 元数据先写入 PostgreSQL；Redis 只作为可删除、可重建的派生缓存。Skill 文件对象必须落在租户共享分区 `files/tenants/{tenant-hash}/skill-packages/...`，不能进入 `users/{user-hash}`；普通用户文件仍使用租户下的用户分区。
- Agent 与 LLM 配置先提交 PostgreSQL，再更新租户隔离且带 TTL 的 Redis 缓存；Redis 失败不影响已提交的事实数据。
- LLM Profile 的 `Modality` 决定执行期是否允许图片内联；多模态输入当前仅限受控大小的 `image/*`，图片字节只在服务端请求作用域读取。
- LLM 和 RAG API Key 以租户绑定的加密值保存在 PostgreSQL，并可能进入服务端 Redis TTL 缓存；管理 API 永不返回该字段。RAG 继续兼容 `ApiKeySecretRef`，执行端通过 `IAgentSecretResolver` 按租户解密或解析。
- Redis 可以保存可过期的会话热副本和分布式锁令牌，但不拥有会话或资产事实；数据库提交成功后才更新热副本，缓存可由数据库回填。

## 禁止事项

- 禁止绕过管理 API 直接修改 Agent 或 LLM 配置表与 Redis 派生缓存
- 禁止通过管理 API、日志或前端状态返回 LLM API Key
- 禁止直接操作其他服务拥有的数据库表
