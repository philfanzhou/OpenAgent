# Conventions: 会话存储链路

## 命名规范

- 接口以 `I` 前缀：`IConversationStore`、`IConversationRepository`
- 热存储实现以技术方案后缀：`InMemoryConversationStore`、`RedisConversationStore`、`DualWriteConversationStore`
- 冷存储实现以 `Repository` 后缀：`SqlServerConversationRepository`、`SqliteConversationRepository`
- 配置以 `Options` 后缀：`ConversationStoreOptions`
- 结果类型以 `Result` 后缀：`AppendResult`
- 指标以 `Metrics` 后缀：`ConversationStoreMetrics`

## 租户隔离

所有存储操作必须以 `tenantId + conversationId` 联合定位，不允许仅凭 `conversationId` 访问数据。

- Redis Key 格式：`conversation:{tenantId}:{conversationId}`
- InMemory Key 格式：`{tenantId}:{conversationId}`
- SQL Server / SQLite：`ConversationMessages` 表含 TenantId 列，`LoadMessagesAsync` 强制 `WHERE TenantId = @TenantId AND ConversationId = @ConversationId`

## 版本控制

- 版本号从 1 开始，每次成功追加自增 1
- `AppendMessagesAsync` 和 `UpdateStatusAsync` 均需传入 `expectedVersion`
- 冲突时调用方应重新加载记录并重试，最多重试一次
- 重试时必须重新分配消息序号（基于最新 MessageCount）

## 消息角色

当前执行链路仅回放三种角色：
- `user` — 用户输入
- `assistant` — Agent 响应
- `tool` — 工具调用结果

其他角色消息在回放时跳过并记录告警日志，不中断执行。

## 时间戳

所有时间字段使用 `DateTimeOffset`，值为 UTC 时间：
- `CreatedAt` — 记录创建时自动设置（init）
- `UpdatedAt` — 每次写入时更新
- `LastMessageAt` — 每次追加消息时更新
- `Timestamp`（ConversationMessage）— 消息产生时自动设置（init）

## 序号分配

消息 `Sequence` 从 1 开始，在会话内严格递增。追加消息时，新消息的 Sequence 基于当前 `MessageCount` 连续分配。冲突重试时需基于重新加载后的最新 MessageCount 重新分配。

## 配置节

`ConversationStoreOptions` 注册为配置节 `ConversationStore`，通过 `IOptions<ConversationStoreOptions>` 注入。

## 序列化

- Redis 存储使用 System.Text.Json，CamelCase 命名策略，不缩进
- 冷归档中 Metadata 字段在 `MetadataJson` 列中独立 JSON 序列化

## 冷归档补偿

DualWrite 模式下，冷归档失败不应阻塞热存储成功。补偿行为：
- 热存储（Redis）写入成功即视为操作成功
- 冷归档失败时记录 Error 级别日志
- 不向调用方抛出冷归档异常
- 冷归档异步执行，不阻塞主路径

## 冷归档 Repository

- 接口：`IConversationRepository : IDisposable`
- 通过 .NET 8 Keyed DI 注册，根据 `ColdArchiveProvider` 配置动态选取实现
- 表：`ConversationRecords`（元数据）+ `ConversationMessages`（行级消息）
- 使用 MERGE INTO / UPSERT 语句实现写入
- 重试使用指数退避（delayMs *= 2），最多重试 ColdArchiveRetryCount 次
- 自动建表（EnsureInitializedAsync，IF NOT EXISTS CREATE TABLE）
- 索引：`IX_Records_Tenant_User`、`IX_Records_Tenant_Agent`、`IX_Messages_Tenant_Time`
- SQL Server 额外使用 TVP（Table-Valued Parameter）进行批量消息插入

## 不做的事

- 不提供查询侧 API（仅执行侧读写）
- 不实现展示态权限控制
- 不引入独立幂等键去重链路
- 不提供面向管理面的恢复和回放接口

## 软删除

- 用户删除会话 = 设置 `IsDeletedByUser = true` + `DeletedAt = now`，不物理删除数据
- 用户侧查询自动过滤 `IsDeletedByUser = 0`，审计侧查询无此过滤
- 软删除通过 `DualWriteConversationStore` 同步到冷归档
- 审计端点返回的消息内容需脱敏处理（PII 字段）

## 会话标题

- 创建会话时取首轮用户消息 Content 前 `TitleTruncateLength` 字符作为初始标题，此步骤必定成功
- `EnableTitleSummarization = true` 时，首轮 LLM 响应后异步调用 LLM 生成摘要标题
- LLM 摘要失败时记录告警日志，保留截取标题，不重试不阻塞业务
- Title 字段最大长度 `TitleTruncateLength`（默认 50 字符）

## 数据分层

- `ConversationMessages` 主表仅保留 `MessageRetentionDays`（默认 90 天）内的活跃数据
- 超期消息迁移到 `ConversationMessagesArchive` 归档表（同库不同表，启用页压缩）
- 迁移由后台 IHostedService 定时任务驱动，按 `ArchiveMigrationIntervalMinutes` 间隔执行
- 每批处理最多 `ArchiveMigrationBatchSize` 个会话，同事务内 INSERT + DELETE
- `ConversationRecords` 元数据表不迁移，永久保留（行数远小于消息表）
- 审计查询需跨主表 + 归档表 UNION 查询
