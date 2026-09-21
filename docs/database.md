# Database — 数据存储唯一事实源

PostgreSQL 是当前 OpenAgent 持久化业务数据的唯一事实源；EF Core migration 位于 `Backend/src/OpenAgent.Infrastructure/Persistence/Migrations/`。`IConversationStore` 与文件资产契约不绑定特定数据库，后续 Provider 可在 Infrastructure 层独立实现。应用进程不自动建表，部署流水线应显式执行 migration。

文件字节保存在 S3/MinIO；对象存储不保存租户、用户、会话或生命周期事实。Redis 如被部署，保存可过期的会话热副本、派生配置缓存并提供分布式锁与 Pub/Sub；这些派生数据均可由数据库回填。Agent 配置以 PostgreSQL 为唯一事实源，Redis 仅保存带 TTL 的租户派生缓存；migration 不会自动导入历史 `agent:config:*` Redis 数据，存量部署必须在切流前单独完成数据迁移。LLM 和 RAG Key 由服务端加密保存，管理 API 返回时脱敏。

## 表清单与关系

| 表 | 说明 |
|---|---|
| `openagent.conversations` | 会话头、所有者、状态与乐观并发版本 |
| `openagent.conversation_messages` | 独立的有序会话消息，元数据使用 `jsonb` |
| `openagent.file_assets` | 用户文件资产元数据与对象键 |
| `openagent.file_share_links` | 文件分享链接（令牌哈希主键、过期与下载计数） |
| `openagent.conversation_file_references` | 文件在会话中的引用 |
| `openagent.message_file_references` | 文件在具体消息中的引用，用于预览和治理 |
| `openagent.agent_configurations` | Agent 基础字段、嵌套能力配置与乐观并发版本；主键 `(TenantId, AgentId)` |
| `openagent.llm_configurations` | LLM 模型连接、ContextTokens、Modality 和服务端加密 Key；主键 `(TenantId, ProfileId)` |
| `openagent.llm_interaction_logs` | 大模型交互请求/响应审计日志（已脱敏），按会话与轮次 TraceId 检索 |

```text
Conversation 1 --- * ConversationMessage
Conversation * --- * FileAsset (conversation_file_references)
ConversationMessage * --- * FileAsset (message_file_references)
FileAsset 1 --- * FileShareLink
```

## conversations 与 conversation_messages

`openagent.conversations` 保存会话归属、状态、标题、时间戳和 `Version`。`Version` 是 EF Core 存储实现用于追加和状态变更的乐观并发边界。

`openagent.conversation_messages` 按 `(ConversationId, Sequence)` 唯一排序，保存角色、内容、工具调用信息、时间戳和可扩展 `MetadataJson(jsonb)`。assistant 终态还可保存 `PromptTokens`、`CompletionTokens`、`TotalTokens`、独立细分的 `CachedInputTokens`/`ReasoningTokens` 与 `ModelId`；这些列均可空，用于诚实表示旧历史、失败或 Provider usage 缺失。消息不嵌入会话 JSON，也不保存文件字节。

用户消息携带的 fileIds（请求层概念，非表列）在同一事务内解析为 `conversation_file_references` 与 `message_file_references` 行；前者支持会话级浏览，后者保证工作台能在准确的消息位置预览或下载文件。

## file_assets

`openagent.file_assets` 是用户文件资产元数据的唯一事实源，由 EF Core migration 建立。每条记录包含租户、所有者、文件名、MIME、长度、SHA-256、实际 S3 `ObjectKey`、来源和 `Pending`/`Ready`/`Failed` 状态。`FileId` 是应用层资产主键；S3 不存在与它等价的独立 ID。

资产独立于会话创建；上传成功后，文件只在随后的用户消息持久化时建立会话和消息引用。删除、分享、复用和未来权限治理都应以资产所有权与引用表为边界，而不是以对象键或临时聊天请求为边界。跨系统分享使用平台自身的分享链接（见下节），不生成对象存储预签名 URL；`ObjectKey` 不作为第三方授权凭据。

## file_share_links

`openagent.file_share_links` 是平台文件分享链接的唯一事实源，由 EF Core migration 建立。每条记录包含令牌哈希（`ShareIdHash`，主键，SHA-256 hex）、`FileId`、租户、所有者、分享时刻的 `ObjectKey`/文件名/MIME/长度快照、`Mode`（temporary/singleUse/longTerm）、`ExpiresAt`、`MaxDownloads`（null 表示不限）与 `DownloadCount`。索引 `(TenantId, OwnerUserId, CreatedAt)` 支撑"查询当前用户的所有分享链接"。

- 数据库不保存明文令牌；下载请求按令牌哈希定位记录，与第三方 API Key 的存储约定一致。
- 核销使用条件 `UPDATE`（未过期且未超次数才 `DownloadCount + 1`）保证原子性，多实例下"单次下载"语义严格。
- 链接不存在、已过期或已用尽下载次数时下载端点统一返回 404，不暴露链接状态。
- 有效期硬上限 365 天（`FileShareOptions.MaxLifetimeLimitSeconds`），不存在永久有效的分享。
- 用户撤销分享为软删除：按 `(ShareIdHash, TenantId, OwnerUserId)` 条件把 `ExpiresAt` 改写为当前时刻（仅对仍有效的记录生效），记录保留、令牌立即 404，且不再出现于查询结果。
- 治理应以 `FileId` 外键（Restrict）与租户边界为准；已失效（过期、用尽或撤销）的记录可按 `ExpiresAt` 清理。

## agent_configurations 与 llm_configurations

Agent 与 LLM 配置以 PostgreSQL 为事实源，共用 `OpenAgentDbContext`，但使用独立的表和 Repository；会话、文件和 Skill 目录仍由各自模块持有。

### llm_configurations

字段稳定且为标量，直接存成数据库列，便于类型约束、查询和迁移：

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

### agent_configurations

TenantId、AgentId 为复合主键；Name、Description、Status、Instructions、MaxTurns、Version、UpdatedAt 都是独立列，Version 用于乐观并发控制。

只有嵌套结构使用 JSONB：ContextPolicyJson（可空）、McpJson、RagJson、SkillsJson、CodeExecutionJson。它们包含选项、ID 集合及兼容旧格式的嵌套实例，当前按整个 Agent 配置读取和更新；没有跨 Agent 查询这些子属性的用例。后续若需要独立查询或管理绑定关系，应再拆为关联表。

CodeExecutionJson 保存 `{ "enabled": false }` 形式的 Agent 代码执行开关；新增列迁移为已有记录填充 `{}`，读取时默认关闭。Runner 服务地址、令牌、镜像和资源限额属于部署配置，不存入 Agent 配置。参见 [capabilities.md](modules/capabilities.md) 的代码执行一节。

### 种子数据

`20260916022059_SeedDefaultAgents` 为 `development` 租户种子两个 `Published` 初始 Agent（Version 1、空能力配置），让未配置任何 Agent 的全新部署开箱即可路由与意图识别：

| AgentId | 用途 |
|---|---|
| `default` | 通用助手。Router 意图识别的 Fallback Agent（`RouterSettings:IntentRecognition:FallbackAgentId` 默认 `default`），也是 Engine 在会话未绑定 Agent 时的兜底 AgentId |
| `intent-router` | 意图分类 Agent。意图识别默认开启，Router 将候选 Agent 列表交给它分类选择 |

租户与 `Authentication:DevelopmentTenantId` 默认值一致，对应开发/Basic 认证链路；其他租户仍需通过管理 API 创建 Agent。种子行只在首次应用迁移时写入，之后被修改或删除不会被迁移恢复。

### 升级

`20260903090000_UseConfigurationColumns` 先增加字段、从旧 ConfigurationJson 回填，再删除整份 JSON 列；旧 `ContextWindowTokens` 迁为 `ContextTokens`，旧 Agent `Snapshot` 状态迁为 `Published`。Down 可将字段重建为旧格式 JSON。

部署时先停止旧版本配置写入、应用迁移，再切换应用；旧版本 Repository 不兼容删除 ConfigurationJson 后的表结构。新版本 Redis key 使用 `v2` 命名空间，避免读取旧字段名的缓存。HTTP 和前端统一使用 `contextTokens`。

实现：`Backend/src/OpenAgent.Infrastructure/Configuration/`、`Backend/src/OpenAgent.Infrastructure/Persistence/OpenAgentDbContext.cs`。

## llm_interaction_logs

记录每一次发往大模型的交互日志（含工具循环的多次迭代与上下文压缩摘要调用），主键 `InteractionId`，外键级联删除指向 `openagent.conversations`（`ConversationId` 可空，兼容无会话上下文的调用）。表只追加，不做更新。

字段定义（C# 契约）：

| 字段名 | C# 类型 | 说明 |
|--------|---------|------|
| InteractionId | string | 交互唯一标识（最大长度 64） |
| TenantId | string | 租户（最大长度 256） |
| UserId | string | 用户（最大长度 256） |
| ConversationId | string? | 会话 ID（最大长度 64，可空） |
| TraceId | string | 轮次追溯键（前端 X-Trace-Id；最大长度 256） |
| AgentId | string? | Agent 标识（最大长度 256） |
| Source | int | 调用来源：0=AgentTurn（对话轮次），1=Compaction（压缩摘要） |
| Provider | string? | 提供商（最大长度 256） |
| ApiFormat | string? | API 协议（最大长度 32） |
| ModelId | string | 模型（最大长度 256） |
| Streamed | bool | 是否流式调用 |
| CallIndex | int | 同一轮内的调用序号，从 0 开始 |
| RequestJson | string? | 脱敏请求载荷（jsonb：messages + options） |
| ResponseJson | string? | 脱敏响应载荷（jsonb：contents + usage），失败可能为空 |
| PromptTokens / CompletionTokens / TotalTokens / CachedInputTokens / ReasoningTokens | int? | Provider usage（要求三项核心计数齐备才视为完整） |
| Status | int | 0=Succeeded，1=Failed，2=Cancelled |
| ErrorMessage | string? | 失败/取消摘要（最大长度 1024） |
| StartedAt | DateTimeOffset | 调用开始时间（timestamptz） |
| DurationMs | int | 调用耗时（毫秒） |

索引：

| 索引 | 用途 |
|------|------|
| `(TenantId, ConversationId, StartedAt)` | 会话维度分页查询（导出/重放） |
| `(TraceId)` | 按轮次定位（跨端追溯） |

安全与保留：

- 载荷写入前经过脱敏投影：API Key 永不参与序列化；`DataContent` 二进制仅记录媒体类型与字节数占位符（附件字节永不落日志）；超长单字段按 `LlmInteraction:MaxContentLength`（默认 100_000 字符）截断。
- 记录失败只降级为警告日志，绝不影响对话主流程；可通过 `LlmInteraction:Enabled=false` 关闭。
- 保留策略跟随会话软删除（数据保留供审计）；当前不做自动清理。

相关迁移：`20260917012452_AddLlmInteractionTraceability`（同时为 `conversation_messages` 增加 `TraceId` 列）。
