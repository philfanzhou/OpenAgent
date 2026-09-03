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
| ApiKey | text | 租户绑定的服务端加密密钥；管理响应清空 |
| Temperature | double precision | 生成温度 |
| ContextTokens | integer | 模型上下文 token 上限 |
| MaxOutputTokens | integer，可空 | 模型输出硬上限，同时作为默认值和输入预留预算 |
| SupportsMaxOutputTokens | boolean | 是否支持发送最大输出参数，默认 true |
| Modality | varchar(32) | Text / Multimodal；目前只开放图片输入 |
| UpdatedAt | timestamptz | 最近保存时间 |

`LlmProviderProfile` 是这条资源的应用数据模型：一个租户可保存多份连接配置，执行请求通过 `llmProfileId` 选择其中一份。它不表示后台注册服务，也不绑定 Agent。`LlmConfig` 是运行时使用的连接参数，不保存显示名称等管理信息。

## Agent 配置

`openagent.agent_configurations` 的 TenantId、AgentId 为复合主键；Name、Description、Status、Instructions、MaxTurns、Version、UpdatedAt 都是独立列，Version 用于乐观并发控制。

`ContextWindowTokens`、`MaxOutputTokens` 为可空 integer 列，保存 Agent 默认执行预算，不绑定模型；执行时与所选模型能力交叉校验。

只有嵌套结构使用 JSONB：ContextPolicyJson（可空）、McpJson、RagJson、SkillsJson。它们包含选项、ID 集合及兼容旧格式的嵌套实例，当前按整个 Agent 配置读取和更新；没有跨 Agent 查询这些子属性的用例。后续若需要独立查询或管理绑定关系，应再拆为关联表。

## 升级

`20260903090000_UseConfigurationColumns` 先增加字段、从旧 ConfigurationJson 回填，再删除整份 JSON 列；旧 `ContextWindowTokens` 迁为 `ContextTokens`，旧 Agent `Snapshot` 状态迁为 `Published`。Down 可将字段重建为旧格式 JSON。

部署时先停止旧版本配置写入、应用迁移，再切换应用；旧版本 Repository 不兼容删除 ConfigurationJson 后的表结构。`20260903100000_AddModelTokenLimits` 增加模型输出上限/支持开关和 Agent 默认预算列，已有模型默认支持参数且输出上限为空，已有 Agent 默认继承模型。应先应用迁移再启动新版应用；该迁移的 Down 仅删除新增字段。

新版本 Redis key 使用 `v3` 命名空间，避免旧应用缓存覆盖新增能力字段。模型 Profile 的 HTTP 字段沿用 `contextTokens`；Agent 默认和单次请求覆盖使用 `contextWindowTokens`。

实现：`Backend/src/OpenAgent.Infrastructure/Configuration/`、`Backend/src/OpenAgent.Infrastructure/Persistence/OpenAgentDbContext.cs`。
