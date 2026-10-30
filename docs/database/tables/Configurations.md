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
| Modality | varchar(32) | Text / Multimodal；目前只开放图片输入 |
| UpdatedAt | timestamptz | 最近保存时间 |

`LlmProviderProfile` 是这条资源的应用数据模型：一个租户可保存多份连接配置，执行请求通过 `llmProfileId` 选择其中一份。它不表示后台注册服务，也不绑定 Agent。`LlmConfig` 是运行时使用的连接参数，不保存显示名称等管理信息。

## Agent 配置

`openagent.agent_configurations` 的 TenantId、AgentId 为复合主键；Name、Description、Status、Instructions、MaxTurns、Version、UpdatedAt 都是独立列，Version 用于乐观并发控制。

只有嵌套结构使用 JSONB：ContextPolicyJson（可空）、McpJson、RagJson、SkillsJson。它们包含选项、ID 集合及兼容旧格式的嵌套实例，当前按整个 Agent 配置读取和更新；没有跨 Agent 查询这些子属性的用例。后续若需要独立查询或管理绑定关系，应再拆为关联表。

历史注记：`20260903140518_AddCodeExecutionConfiguration` 曾引入 CodeExecutionJson 列保存 Agent 代码执行开关；独立 Runner 移除后，`20261028091823_RemoveCodeExecutionConfiguration` 已删除该列（Down 迁移可恢复为 `"{}"`）。

## 种子数据

`20260916022059_SeedDefaultAgents` 为 `development` 租户种子两个 `Published` 初始 Agent（Version 1、空能力配置），让未配置任何 Agent 的全新部署开箱即可路由与意图识别：

| AgentId | 用途 |
|---|---|
| `default` | 通用助手。Router 意图识别的 Fallback Agent（`RouterSettings:IntentRecognition:FallbackAgentId` 默认 `default`），也是 Engine 在会话未绑定 Agent 时的兜底 AgentId |
| `intent-router` | 意图分类 Agent。意图识别默认开启，Router 将候选 Agent 列表交给它分类选择 |

租户与 `Authentication:DevelopmentTenantId` 默认值一致，对应开发/Basic 认证链路；其他租户仍需通过管理 API 创建 Agent。种子行只在首次应用迁移时写入，之后被修改或删除不会被迁移恢复。

## 升级

`20260903090000_UseConfigurationColumns` 先增加字段、从旧 ConfigurationJson 回填，再删除整份 JSON 列；旧 `ContextWindowTokens` 迁为 `ContextTokens`，旧 Agent `Snapshot` 状态迁为 `Published`。Down 可将字段重建为旧格式 JSON。

部署时先停止旧版本配置写入、应用迁移，再切换应用；旧版本 Repository 不兼容删除 ConfigurationJson 后的表结构。新版本 Redis key 使用 `v2` 命名空间，避免读取旧字段名的缓存。HTTP 和前端统一使用 `contextTokens`。

实现：`Backend/src/OpenAgent.Framework/Configuration/`、`Backend/src/OpenAgent.Framework/Persistence/OpenAgentDbContext.cs`。


## Runtime 与本机持久化补充

`runtime_configurations` 使用 `(TenantId, RuntimeId)` 复合主键及 `DefinitionJson jsonb`，保存 Runtime 名称、Provider 和 TurnTimeoutSeconds；资源绑定不在该记录中。`mcp_configurations` 使用 `(TenantId, Name)` 和 `DefinitionJson jsonb` 保存原有 MCP 定义。运行时读取始终按当前认证租户过滤。

`agent_configurations.OpenCodeJson jsonb NOT NULL DEFAULT '{}'` 持久化原有 Agent 内嵌模型连接，其中 API Key 由配置服务加密、读取时解密、管理端响应脱敏；原有 MCP/Skill 列和绑定不变。迁移为 `AddRuntimeProfilesAndDurableLocalConfiguration`。迁移历史表固定为 `public.__EFMigrationsHistory`。


## 资源策略

迁移 `20261029155656_AddResourcePolicies` 新增 `openagent.resource_policies`，不修改已有资源表。

| 列 | 类型 | 用途 |
|---|---|---|
| TenantId | varchar(256) | 当前认证租户 |
| ResourceType | varchar(32) | Agent / Model / Mcp / Skill / Tool / Function |
| ResourceId | varchar(512) | 资源业务 ID |
| Revision | bigint | 从 1 起的乐观并发修订；保存必须匹配上次读到的值 |
| RulesJson | jsonb | 动作、用户、角色与显式拒绝规则，不保存凭据 |

前三列组成主键；Revision 为并发 token。不存在策略和存在空策略含义不同：前者由兼容/严格开关决定，后者始终拒绝。策略行不能证明资源存在或属于租户，授权仓储另读真实资源配置行。资源删除后策略仍保留，避免重新创建时意外恢复宽松权限。策略不经 Redis 或本地授权缓存。

策略编辑采用条件更新，创建竞争由主键唯一约束处理，冲突返回 409；Down 只删除新增表，先备份策略再执行。迁移、资源 ID、启用顺序、撤权上限与回退见 [安全域](../../modules/security.md#迁移与回退)。
