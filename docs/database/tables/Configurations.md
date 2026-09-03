# 配置表

Agent 与 LLM 配置以 PostgreSQL 为事实源，共用 `OpenAgentDbContext`，但使用独立的表和 Repository；会话、文件和 Skill 目录仍由各自模块持有。

## LLM 配置

`openagent.llm_configurations` 的字段稳定且为标量，直接存成数据库列，便于类型约束、查询和迁移。

| 列 | 类型 | 用途 |
|---|---|---|
| TenantId、ProfileId | varchar(256) | 租户和配置 ID，复合主键 |
| Name | text | 显示名称 |
| Format | varchar(32) | OpenAIChatCompletions / OpenAIResponses / AnthropicMessages |
| ModelId | text | 供应商的模型标识 |
| Endpoint | text | 模型 API 地址 |
| ApiKey | text | 服务端明文密钥；管理响应清空 |
| Temperature | double precision | 生成温度 |
| ContextTokens | integer | 模型上下文 token 上限 |
| Modality | varchar(32) | Text / Multimodal；目前只开放图片输入 |
| UpdatedAt | timestamptz | 最近保存时间 |

`LlmProviderProfile` 是这条资源的应用数据模型：一个租户可保存多份连接配置，执行请求通过 `llmProfileId` 选择其中一份。它不表示后台注册服务，也不绑定 Agent。`LlmConfig` 是运行时使用的连接参数，不保存显示名称等管理信息。

## Agent 配置

`openagent.agent_configurations` 的 TenantId、AgentId 为复合主键；Name、Description、Status、Instructions、MaxTurns、Version、UpdatedAt 都是独立列，Version 用于乐观并发控制。

只有嵌套结构使用 JSONB：ContextPolicyJson（可空）、McpJson、RagJson、SkillsJson。它们包含选项、ID 集合及兼容旧格式的嵌套实例，当前按整个 Agent 配置读取和更新；没有跨 Agent 查询这些子属性的用例。后续若需要独立查询或管理绑定关系，应再拆为关联表。

## 升级

`20260903090000_UseConfigurationColumns` 先增加字段、从旧 ConfigurationJson 回填，再删除整份 JSON 列；旧 `ContextWindowTokens` 迁为 `ContextTokens`，旧 Agent `Snapshot` 状态迁为 `Published`。Down 可将字段重建为旧格式 JSON。

部署时先停止旧版本配置写入、应用迁移，再切换应用；旧版本 Repository 不兼容删除 ConfigurationJson 后的表结构。新版本 Redis key 使用 `v2` 命名空间，避免读取旧字段名的缓存。HTTP 和前端统一使用 `contextTokens`。

实现：`Backend/src/OpenAgent.Infrastructure/Configuration/`、`Backend/src/OpenAgent.Infrastructure/Persistence/OpenAgentDbContext.cs`。
