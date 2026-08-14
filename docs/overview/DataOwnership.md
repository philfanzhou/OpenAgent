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
| AgentConfig | Agent.Matrix (通过 IAgentConfigProvider) | 获取 Agent 配置（MCP、官方 Skill 包绑定、RAG 配置等） |
| LlmProviderProfile | ILlmRegistry (内存注册) | 获取 LLM 连接配置 |
| RagInstanceConfig | IRagRegistry (内存注册) | 获取 RAG 实例配置 |
| Agent Skill ZIP | S3 兼容对象存储 | 保存官方 `SKILL.md` 包；运行时解压到请求级临时目录并由 MAF `AgentSkillsProvider` 读取 |

## 持久化规则

- ConversationRecord、ConversationMessage 与 FileAsset 只写入 PostgreSQL。
- 文件字节只写入 S3 兼容对象存储；对象存储不拥有用户、租户或会话事实。
- Redis 可以保存可过期的会话热副本和分布式锁令牌，但不拥有会话或资产事实；数据库提交成功后才更新热副本，缓存可由数据库回填。

## 禁止事项

- 禁止直接写入 Agent.Matrix 管理的配置数据
- 禁止直接操作其他服务拥有的数据库表
