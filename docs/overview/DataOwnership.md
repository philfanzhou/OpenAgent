# Data Ownership — OpenAgent

## 本服务拥有的数据实体

| 实体 | 存储位置 | 说明 |
|------|----------|------|
| ConversationRecord | PostgreSQL | 会话主记录、状态和乐观并发版本 |
| ConversationMessage | PostgreSQL | 独立有序消息和消息级文件引用 |
| FileAsset | PostgreSQL | 用户文件元数据、归属、状态与会话引用 |
| File Object | S3 兼容对象存储 | 文件原始字节；不保存授权和会话事实 |

## 引用的外部数据（只读）

| 数据 | 来源 | 用途 |
|------|------|------|
| AgentConfig | Agent.Matrix (通过 IAgentConfigProvider) | 获取 Agent 绑定关系（MCP Server ID、官方 Skill ID、LLM Provider ID + Model ID、RAG 配置等） |
| LlmProviderProfile | Redis `llm:published:index` + ILlmRegistry (内存注册) | 获取共享 LLM 协议、Endpoint、密钥和默认参数；具体 Model ID 由 Agent 指定 |
| McpServerConfig | Redis `mcp:published:index` + IMcpRegistry (内存注册) | 获取独立 MCP Server 连接配置；不复制到 Agent |
| RagInstanceConfig | IRagRegistry (内存注册) | 获取 RAG 实例配置 |
| Agent Skill 文件目录 | S3 兼容对象存储 | ZIP/MD 上传后按解压目录写入文件对象；运行时 materialize 到请求级临时目录并由 MAF `AgentSkillsProvider` 读取 |
| Skill 目录元数据 | PostgreSQL `SkillDefinitions`；Redis 为派生缓存 | 按租户保存可绑定的 Skill 元数据；不表示某个 Agent 已绑定 |

## 持久化规则

- ConversationRecord、ConversationMessage 与 FileAsset 只写入 PostgreSQL。
- 文件字节只写入 S3 兼容对象存储；对象存储不拥有用户、租户或会话事实。
- Skill 元数据先写入 PostgreSQL；Redis 只作为可删除、可重建的派生缓存。Skill 文件对象必须落在 `files/tenants/{tenant-hash}/users/{user-hash}/...` 租户分区。
- Redis 可以保存可过期的会话热副本和分布式锁令牌，但不拥有会话或资产事实；数据库提交成功后才更新热副本，缓存可由数据库回填。

## 禁止事项

- 禁止直接写入 Agent.Matrix 管理的配置数据
- 禁止直接操作其他服务拥有的数据库表
