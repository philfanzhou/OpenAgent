# Data Models: 会话存储链路

## ConversationRecord

会话主记录，承载会话级元数据与消息列表。

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| ConversationId | string | 是 | 会话唯一标识，由调用方传入 |
| TenantId | string | 是 | 租户隔离键，与 ConversationId 联合定位会话 |
| UserId | string | 是 | 发起会话的用户标识 |
| AgentId | string | 否 | 处理会话的 Agent 标识 |
| TraceId | string | 否 | 分布式追踪标识 |
| Version | int | — | 乐观并发版本号，初始为 1，每次追加自增 |
| Status | ConversationStatus | — | 会话状态，初始为 Running |
| CreatedAt | DateTimeOffset | — | 记录创建时间，UTC |
| UpdatedAt | DateTimeOffset | — | 记录最后更新时间，UTC |
| LastMessageAt | DateTimeOffset | — | 最后一条消息时间，UTC |
| MessageCount | int | — | 消息总数，随追加更新 |
| Messages | List\<ConversationMessage\> | — | 消息列表，按 Sequence 升序 |
| Title | string? | 否 | 会话标题，首轮取用户消息截取，后续异步 LLM 摘要更新 |
| IsDeletedByUser | bool | — | 用户软删除标记，true 表示用户不可见不可搜索，数据保留供审计 |
| DeletedAt | DateTimeOffset? | — | 用户软删除时间，UTC，null 表示未删除 |
| ArchivedAt | DateTimeOffset | — | 归档入库时间，UTC，用于数据分层迁移判断 |

## ConversationMessage

单条消息记录，承载对话内容与工具调用信息。

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| MessageId | string | 是 | 消息唯一标识 |
| Sequence | int | 是 | 会话内递增序号，从 1 开始 |
| Role | string | 是 | 消息角色，当前回放仅处理 user / assistant / tool |
| Content | string | 是 | 消息文本内容 |
| ToolCallId | string | 否 | 工具调用关联标识，tool 角色消息使用 |
| ToolName | string | 否 | 工具名称，tool 角色消息使用 |
| Timestamp | DateTimeOffset | — | 消息产生时间，UTC |
| Metadata | Dictionary\<string, string\> | 否 | 扩展元数据键值对 |

## ConversationStatus

会话状态枚举：

| 值 | 名称 | 说明 |
|----|------|------|
| 0 | Running | 会话进行中 |
| 1 | Completed | 会话正常完成 |
| 2 | Failed | 会话执行失败 |
| 3 | Cancelled | 会话被取消 |

## AppendResult

追加操作结果：

| 字段 | 类型 | 说明 |
|------|------|------|
| Success | bool | 是否追加成功 |
| NewVersion | int | 成功后的新版本号 |
| NewMessageCount | int | 成功后的消息总数 |
| ConflictReason | string? | 失败时的冲突原因 |

工厂方法：
- `AppendResult.Ok(newVersion, newMessageCount)` — 成功
- `AppendResult.Conflict(reason)` — 版本冲突

## ConversationStoreOptions

存储配置选项，配置节名称为 `ConversationStore`：

| 字段 | 类型 | 默认值 | 说明 |
|------|------|--------|------|
| MaxHistoryMessages | int | 20 | 执行侧历史消息窗口大小 |
| RedisTtlMinutes | int | 30 | Redis 会话记录 TTL（分钟） |
| RedisConnectionString | string? | null | Redis 连接字符串，为空则使用 InMemory |
| EnableColdArchive | bool | true | 是否启用数据库冷归档 |
| ColdArchiveConnectionString | string? | null | 数据库连接字符串 |
| ColdArchiveProvider | string | SqlServer | 冷归档提供器：SqlServer 或 Sqlite |
| ColdArchiveRetryCount | int | 3 | 冷归档写入重试次数 |
| ColdArchiveRetryDelayMs | int | 1000 | 冷归档写入重试延迟基础值（毫秒，指数退避） |

## ConversationStoreMetrics

存储指标追踪（public sealed）：

| 指标 | 说明 |
|------|------|
| Hits | 缓存命中次数 |
| Misses | 缓存未中次数 |
| MessagesLoaded | 加载的消息总数 |
| MessagesWritten | 写入的消息总数 |
| ReadFailures | 读取失败次数 |
| WriteFailures | 写入失败次数 |
| ColdArchiveSuccesses | 冷归档成功次数 |
| ColdArchiveFailures | 冷归档失败次数 |

| ColdArchiveLatencySum | 冷归档累计延迟（毫秒） |
| ColdArchiveOpCount | 冷归档操作次数 |

所有计数器使用 Interlocked 操作保证线程安全。

## 冷归档表结构

`IConversationRepository` 将消息规范化为行级存储：

### ConversationRecords

会话元数据表（以 ConversationId 为主键）：

| 列 | 类型 (SQL Server) | 类型 (SQLite) | 说明 |
|----|-------------------|---------------|------|
| ConversationId | NVARCHAR(128) | TEXT | 主键 |
| TenantId | NVARCHAR(128) | TEXT | 租户隔离键 |
| UserId | NVARCHAR(128) | TEXT | 用户标识 |
| AgentId | NVARCHAR(128) | TEXT | Agent 标识（可空） |
| TraceId | NVARCHAR(128) | TEXT | 追踪标识（可空） |
| Version | INT | INTEGER | 版本号 |
| Status | INT | INTEGER | 会话状态 |
| CreatedAt | DATETIMEOFFSET | TEXT | 创建时间（ISO 8601） |
| UpdatedAt | DATETIMEOFFSET | TEXT | 更新时间 |
| LastMessageAt | DATETIMEOFFSET | TEXT | 最后消息时间 |
| MessageCount | INT | INTEGER | 消息总数 |
| Title | NVARCHAR(256) | TEXT | 会话标题（可空） |
| IsDeletedByUser | BIT | INTEGER | 用户软删除标记（默认 0） |
| DeletedAt | DATETIMEOFFSET | TEXT | 用户删除时间（可空） |
| ArchivedAt | DATETIMEOFFSET | TEXT | 归档入库时间 |

索引：`(TenantId, UserId, UpdatedAt)`、`(TenantId, AgentId, UpdatedAt)`、`(TenantId, IsDeletedByUser, LastMessageAt)`、`(ArchivedAt)`

### ConversationMessages

消息行级表（以 ConversationId + Sequence 为复合主键），保留 90 天活跃数据：

| 列 | 类型 (SQL Server) | 类型 (SQLite) | 说明 |
|----|-------------------|---------------|------|
| ConversationId | NVARCHAR(128) | TEXT | 复合主键 |
| Sequence | INT | INTEGER | 复合主键 |
| MessageId | NVARCHAR(128) | TEXT | 消息唯一标识 |
| Role | NVARCHAR(16) | TEXT | 消息角色 |
| Content | NVARCHAR(MAX) | TEXT | 消息内容 |
| ToolCallId | NVARCHAR(128) | TEXT | 工具调用 ID（可空） |
| ToolName | NVARCHAR(128) | TEXT | 工具名称（可空） |
| Timestamp | DATETIMEOFFSET | TEXT | 消息时间 |
| MetadataJson | NVARCHAR(MAX) | TEXT | 元数据 JSON（可空） |
| TenantId | NVARCHAR(128) | TEXT | 租户隔离键 |

索引：`(TenantId, Timestamp)`

SQL Server 额外使用 TVP（Table-Valued Parameter）类型 `dbo.ConversationMessageType` 进行批量消息插入。

### ConversationMessagesArchive（SQL Server 专有）

归档消息表，超期消息从 `ConversationMessages` 迁移至此，表结构与 `ConversationMessages` 完全一致：

| 列 | 类型 (SQL Server) | 说明 |
|----|-------------------|------|
| ConversationId | NVARCHAR(128) | 复合主键 |
| Sequence | INT | 复合主键 |
| MessageId | NVARCHAR(128) | 消息唯一标识 |
| Role | NVARCHAR(16) | 消息角色 |
| Content | NVARCHAR(MAX) | 消息内容 |
| ToolCallId | NVARCHAR(128) | 工具调用 ID（可空） |
| ToolName | NVARCHAR(128) | 工具名称（可空） |
| Timestamp | DATETIMEOFFSET | 消息时间 |
| MetadataJson | NVARCHAR(MAX) | 元数据 JSON（可空） |
| TenantId | NVARCHAR(128) | 租户隔离键 |

- 启用页压缩（Page Compression）
- 索引：`(TenantId, Timestamp)`、`(ConversationId)`
- 只写不读（审计调取时查询）
