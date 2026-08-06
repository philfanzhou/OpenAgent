# Data Ownership — Agent.Core

## 本服务拥有的数据实体

| 实体 | 存储位置 | 说明 |
|------|----------|------|
| ConversationRecord | Redis (热) / SQL Server (冷) | 会话主记录 |
| ConversationMessage | Redis (热) / SQL Server (冷，JSON列) | 会话消息，嵌入在 ConversationRecord 中 |

## 引用的外部数据（只读）

| 数据 | 来源 | 用途 |
|------|------|------|
| AgentConfig | Agent.Matrix (通过 IAgentConfigProvider) | 获取 Agent 配置（引擎类型、技能列表、RAG 配置等） |
| LlmProviderProfile | ILlmRegistry (内存注册) | 获取 LLM 连接配置 |
| RagInstanceConfig | IRagRegistry (内存注册) | 获取 RAG 实例配置 |
| SkillDescriptor | SkillCatalog (运行时发现) | 获取技能元数据 |

## 双写规则

- ConversationRecord/Message：Redis（热）+ SQL Server（冷）双写
  - 热存储为写入主路径，冷归档为异步补偿
  - 冷归档失败不阻塞主流程，需后续补偿

## 禁止事项

- 禁止直接写入 Agent.Matrix 管理的配置数据
- 禁止直接操作其他服务拥有的数据库表
