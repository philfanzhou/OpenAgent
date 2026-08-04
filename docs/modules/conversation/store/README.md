
## Feature


## 用户故事

作为执行内核，我希望会话消息被可靠持久化，以便跨请求保持对话上下文。

## 概述

会话存储链路负责 Agent.Core 执行侧的消息持久化与读取，确保多轮对话的上下文在请求之间完整保留。存储层采用渐进式分层架构，从内存存储到 Redis 热存储再到双写冷归档，按配置自动选择。

## 核心能力

- 按 `conversationId + tenantId` 读取已有会话及历史消息
- 创建新会话主记录
- 追加本轮新增消息（user / assistant / tool）
- 更新会话状态（Running / Completed / Failed / Cancelled）
- 乐观并发控制，版本冲突时自动重试

## 当前状态

**已实现** — InMemory / Redis / DualWrite 三级存储链路均已落地，乐观并发控制已生效。

## 当前限制

- 无查询侧 API（仅执行侧读写）
- 无展示态权限控制
- 无独立幂等键（IdempotencyKey）去重链路
- 无面向管理面的恢复和回放接口
- ConversationStoreOptions 中无 RedisConnectionString 字段（由 ServiceExtensions 从配置单独读取）

## 规划中

- **软删除与审计保留**：用户删除会话仅标记 `IsDeletedByUser`，数据物理保留供审计查询，审计端点独立路由并返回脱敏数据
- **会话标题生成**：首轮消息截取为初始标题（必定成功），异步 LLM 摘要更新（失败告警不阻塞）
- **数据分层管理**：`ConversationMessages` 主表保留 90 天活跃数据，超期迁移到 `ConversationMessagesArchive` 归档表（页压缩），后台定时任务驱动，控制单表数据量

## Architecture


## 存储分层

存储链路采用三级渐进式架构，按配置自动选择实现：

```
┌─────────────────────────────────────────────────┐
│              IConversationStore                  │
│                 (热存储接口层)                     │
├─────────────────────────────────────────────────┤
│  InMemoryConversationStore                      │
│  ├─ ConcurrentDictionary<string, ConversationRecord> │
│  └─ 适用：开发/测试环境，无 Redis 配置时回退       │
├─────────────────────────────────────────────────┤
│  RedisConversationStore                         │
│  ├─ Redis String 存储会话记录（JSON 序列化）       │
│  ├─ 带 Metrics 追踪（读写延迟/命中率）            │
│  ├─ TTL 过期策略（RedisTtlMinutes，默认 30 分钟） │
│  ├─ Key 格式：conversation:{tenantId}:{conversationId} │
│  └─ 适用：生产环境，仅热存储                      │
├─────────────────────────────────────────────────┤
│  DualWriteConversationStore                     │
│  ├─ 热路径：Redis（同 RedisConversationStore）    │
│  ├─ 冷路径：IConversationRepository              │
│  │   ├─ SqlServerConversationRepository          │
│  │   └─ SqliteConversationRepository             │
│  ├─ MERGE INTO / UPSERT + 指数退避重试           │
│  ├─ 冷归档失败时补偿：热存储仍成功，记录告警       │
│  └─ 适用：生产环境，EnableColdArchive=true        │
└─────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────┐
│           IConversationRepository                │
│              (冷存储接口层)                        │
├─────────────────────────────────────────────────┤
│  SqlServerConversationRepository                │
│  ├─ 行级消息表：ConversationMessages              │
│  ├─ 批量写入：TVP (Table-Valued Parameter)       │
│  ├─ 自动建表（EnsureInitializedAsync）            │
│  └─ MERGE INTO + 指数退避重试                    │
├─────────────────────────────────────────────────┤
│  SqliteConversationRepository                   │
│  ├─ 行级消息表：ConversationMessages              │
│  ├─ 批量写入：事务逐行 INSERT OR IGNORE          │
│  ├─ 自动建表（EnsureInitializedAsync）            │
│  └─ UPSERT + 指数退避重试                        │
└─────────────────────────────────────────────────┘
```

## 选择逻辑

由 ServiceExtensions.AddAgentCore 根据配置决定：

### 热存储选择

```
未配置 ConversationStore:RedisConnectionString → InMemoryConversationStore
配置 Redis + EnableColdArchive=false → RedisConversationStore
配置 Redis + EnableColdArchive=true  → DualWriteConversationStore(Redis, Repository)
```

### 冷存储选择

冷归档实现通过 .NET 8 Keyed DI 注册，根据 `ColdArchiveProvider` 配置动态选取：

```
ColdArchiveProvider="SqlServer" → SqlServerConversationRepository
ColdArchiveProvider="Sqlite"    → SqliteConversationRepository
```

两个实现始终注册在 DI 容器中（连接仅在首次使用时建立）：
```csharp
services.AddKeyedSingleton<IConversationRepository, SqlServerConversationRepository>("SqlServer");
services.AddKeyedSingleton<IConversationRepository, SqliteConversationRepository>("Sqlite");
```

## 读写流程

### 读取流程

1. 执行前检查上下文是否包含 `ConversationId` 和 `TenantId`
2. 调用 `GetRecordAsync` 加载完整会话记录
3. 若会话不存在，调用 `CreateAsync` 创建新会话主记录
4. 若会话存在，取最近 `MaxHistoryMessages` 条历史消息回放
5. 未知角色消息跳过并记录告警日志

### 写回流程

- **非流式成功**：写回最终 `assistant` 消息
- **流式成功**：流结束后写回 `assistant` 消息
- **流式取消**：写回已产生的 partial `assistant`，状态标记 `Cancelled`
- **流式失败**：写回已产生的 partial `assistant`，状态标记 `Failed`
- **非流式失败/取消**：保留本轮已产生消息并更新状态

## 并发控制

采用版本号驱动的乐观并发：

1. `AppendMessagesAsync` 携带 `expectedVersion`
2. 存储层比对 `expectedVersion` 与当前 `record.Version`
3. 匹配则追加消息，`Version` 自增 1
4. 不匹配则返回 `AppendResult.Conflict`，携带 `ConflictReason`
5. 调用方收到冲突后重新加载记录并重试一次，重试时重新分配消息序号

## 冷归档

### IConversationRepository

冷归档将消息规范化为行级存储，包含两张表：

- **ConversationRecords**：会话元数据（ConversationId 为主键）
- **ConversationMessages**：消息行级表（ConversationId + Sequence 为复合主键），每条消息一行

冷归档操作：
- `ArchiveAsync`：UPSERT 会话元数据
- `ArchiveMessagesAsync`：批量写入消息行
- `GetRecordAsync`：读取记录 + 加载全部消息
- `LoadMessagesAsync`：按租户和会话 ID 加载消息（强制 tenantId 隔离）

### DualWriteConversationStore 补偿

- 热存储（Redis）写入成功即视为操作成功
- 冷归档失败时记录 Error 日志
- 不向调用方抛出冷归档异常
- 冷归档异步执行（`_ = ArchiveWithCompensationAsync`），不阻塞主路径

### 重试策略

- MERGE INTO / UPSERT 操作支持指数退避重试（delayMs *= 2）
- 最多重试 ColdArchiveRetryCount 次
- 归档失败时通过 ConversationStoreMetrics 记录失败指标

## 配置

所有存储行为由 `ConversationStoreOptions` 控制，配置节名称为 `ConversationStore`：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| MaxHistoryMessages | 20 | 执行侧历史消息窗口大小 |
| RedisTtlMinutes | 30 | Redis 会话记录 TTL |
| RedisConnectionString | null | Redis 连接字符串，为空则使用 InMemory |
| EnableColdArchive | true | 是否启用数据库冷归档 |
| ColdArchiveConnectionString | null | 数据库连接字符串 |
| ColdArchiveProvider | SqlServer | 冷归档提供器：SqlServer 或 Sqlite |
| ColdArchiveRetryCount | 3 | 冷归档写入重试次数 |
| ColdArchiveRetryDelayMs | 1000 | 冷归档写入重试延迟（基础值，指数退避） |
| MessageRetentionDays | 90 | 消息活跃期保留天数，超期迁移到归档消息表 |
| ArchiveMigrationIntervalMinutes | 60 | 数据分层迁移任务执行间隔（分钟） |
| ArchiveMigrationBatchSize | 100 | 每次迁移批量处理的最大会话数 |
| TitleTruncateLength | 50 | 会话标题截取的最大字符数 |
| EnableTitleSummarization | true | 是否启用 LLM 异步生成会话摘要标题 |

## 软删除与审计保留

### 软删除机制

用户删除会话时不物理删除数据，仅设置软删除标记：

- 设置 `IsDeletedByUser = true` + `DeletedAt = now`
- 通过 `UpdateStatusAsync` 或专用 `SoftDeleteAsync` 方法写入
- DualWrite 同步到冷归档

### 可见性隔离

| 场景 | 过滤条件 | 说明 |
|------|---------|------|
| 用户侧 List/Search | `IsDeletedByUser = 0` | 用户看不到已删除的会话 |
| 审计侧 List/Search | 无 `IsDeletedByUser` 过滤 | 审计接口可查全量，含已删除 |
| 审计侧消息内容 | 脱敏处理 | 审计端点返回的消息内容需脱敏 |

### 审计端点

独立的审计 API 端点，与用户端点隔离：

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/audit/conversations?skip=0&take=20` | 列出全量会话（含已删除） |
| GET | `/audit/conversations/search?keyword=xxx&skip=0&take=20` | 搜索全量会话消息内容（需时间范围） |
| GET | `/audit/conversations/{conversationId}` | 获取单个会话详情（含已删除） |

审计端点返回的消息内容需脱敏处理（如邮箱、手机号、身份证等 PII 字段）。

## 会话标题生成

### 降级策略

```
会话创建时
  → 取首轮用户消息 Content 前 TitleTruncateLength 字符作为初始 Title
  → 此步骤必定成功，不依赖 LLM

首轮 LLM 响应后（异步）
  → EnableTitleSummarization=true 时，调用 LLM 生成摘要标题
  → 成功 → 更新 Title 字段
  → 失败 → 记录告警日志，保留截取标题，不影响业务
```

### 约束

- 初始标题生成不依赖任何外部服务，确保必定成功
- LLM 摘要标题为异步 fire-and-forget，不阻塞主流程
- LLM 调用失败时告警但不重试不阻塞
- Title 字段最大长度由 `TitleTruncateLength` 控制

## 数据分层管理

### 分层结构

冷归档层按数据活跃度分两层，同库不同表：

```
ConversationRecords（会话元数据，永久保留）
  ├── 不分层，行数远小于消息表（1:20+ 比例）
  └── 含 IsDeletedByUser / ArchivedAt 字段

ConversationMessages（活跃消息，默认保留 90 天）
  ├── 高频读写，索引齐全
  └── 数据量可控

ConversationMessagesArchive（归档消息，超期迁移）
  ├── 只写不读（审计调取时查）
  ├── 启用页压缩（Page Compression），空间降 50-70%
  └── 仅需 ConversationId + TenantId + Timestamp 索引
```

### 迁移机制

后台定时任务（IHostedService）按 `ArchiveMigrationIntervalMinutes` 间隔执行：

1. 扫描 `ConversationRecords` 中 `ArchivedAt < now - MessageRetentionDays` 的会话
2. 每批最多处理 `ArchiveMigrationBatchSize` 个会话
3. 对每个会话，同事务内：
   - `INSERT INTO ConversationMessagesArchive SELECT * FROM ConversationMessages WHERE ConversationId = @id`
   - `DELETE FROM ConversationMessages WHERE ConversationId = @id`
4. 元数据记录保留在 `ConversationRecords`，不迁移
5. 记录迁移日志和 Metrics

### 数据量控制

按 1000 活跃用户、日均 10 轮对话、每轮 20 条消息估算：

| 层级 | 周期 | 行数估算 | 空间估算 |
|------|------|---------|---------|
| Redis 热存储 | 30 分钟 TTL | ~2 万条 | 可控 |
| ConversationMessages 主表 | 90 天 | ~1800 万行 | ~5-10 GB |
| ConversationMessagesArchive | 永久 | 持续增长（压缩后） | 可控 |
| ConversationRecords | 永久 | ~90 万行/年 | < 1 GB |

## Data Models


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

## API


## IConversationStore

会话存储的核心接口，所有存储实现均遵循此契约。

### GetMessagesAsync

获取会话的最近 N 条消息，按 Sequence 升序返回。

```csharp
Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
    string tenantId,
    string conversationId,
    int maxMessages,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| conversationId | string | 会话标识 |
| maxMessages | int | 最多返回消息条数 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：按 Sequence 升序排列的消息列表，最多 `maxMessages` 条。

---

### GetRecordAsync

获取完整会话记录（含元数据和全部消息）。

```csharp
Task<ConversationRecord?> GetRecordAsync(
    string tenantId,
    string conversationId,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| conversationId | string | 会话标识 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：会话记录，不存在时返回 `null`。

---

### CreateAsync

创建新会话记录。如果已存在则返回 `false`。

```csharp
Task<bool> CreateAsync(
    ConversationRecord record,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| record | ConversationRecord | 待创建的会话记录 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：创建成功返回 `true`，已存在返回 `false`。

---

### AppendMessagesAsync

追加消息到已有会话。使用乐观锁：如果 `expectedVersion` 与存储版本不匹配则失败。成功后 Version 自增 1。

```csharp
Task<AppendResult> AppendMessagesAsync(
    string tenantId,
    string conversationId,
    int expectedVersion,
    IReadOnlyList<ConversationMessage> messages,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| conversationId | string | 会话标识 |
| expectedVersion | int | 期望的当前版本号 |
| messages | IReadOnlyList\<ConversationMessage\> | 待追加的消息列表 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：`AppendResult`，包含 Success / NewVersion / NewMessageCount / ConflictReason。

---

### UpdateStatusAsync

更新会话状态（Running / Completed / Failed / Cancelled）。

```csharp
Task<bool> UpdateStatusAsync(
    string tenantId,
    string conversationId,
    ConversationStatus status,
    int expectedVersion,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| conversationId | string | 会话标识 |
| status | ConversationStatus | 目标状态 |
| expectedVersion | int | 期望的当前版本号 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：更新成功返回 `true`，版本冲突返回 `false`。

---

### ListConversationsAsync

列出指定租户的会话，按 `LastMessageAt` 降序排列，支持分页。返回的记录不含消息体（`Messages` 为空）。

```csharp
Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
    string tenantId,
    int skip,
    int take,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| skip | int | 跳过前 N 条记录 |
| take | int | 最多返回 N 条记录 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：按 `LastMessageAt` 降序排列的会话记录列表（不含消息体）。

---

### SearchConversationsAsync

按关键词搜索会话消息内容，不区分大小写。返回的记录不含消息体。

```csharp
Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
    string tenantId,
    string keyword,
    int skip,
    int take,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| keyword | string | 搜索关键词 |
| skip | int | 跳过前 N 条记录 |
| take | int | 最多返回 N 条记录 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：匹配关键词的会话记录列表（不含消息体）。

---

### GetMessagesPagedAsync

分页获取会话消息，按 `Sequence` 升序排列。

```csharp
Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(
    string tenantId,
    string conversationId,
    int skip,
    int take,
    CancellationToken cancellationToken = default);
```

| 参数 | 类型 | 说明 |
|------|------|------|
| tenantId | string | 租户标识 |
| conversationId | string | 会话标识 |
| skip | int | 跳过前 N 条消息 |
| take | int | 最多返回 N 条消息 |
| cancellationToken | CancellationToken | 取消令牌 |

**返回**：按 `Sequence` 升序排列的消息列表。

---

## IConversationQueryService

查询侧门面服务，实现热存储 + 冷归档合并查询策略。热存储优先，冷归档补充，按 `ConversationId` 去重（热存储版本优先）。

### ListConversationsAsync

```csharp
Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
    string tenantId, int skip, int take, CancellationToken cancellationToken = default);
```

**行为**：合并热存储和冷归档结果，去重后按 `LastMessageAt` 降序排列，应用分页。冷归档查询失败时优雅降级到热存储结果。

### GetConversationAsync

```csharp
Task<ConversationRecord?> GetConversationAsync(
    string tenantId, string conversationId, CancellationToken cancellationToken = default);
```

**行为**：优先查热存储，未找到时回退冷归档（同时加载消息体）。

### GetMessagesPagedAsync

```csharp
Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(
    string tenantId, string conversationId, int skip, int take, CancellationToken cancellationToken = default);
```

**行为**：优先查热存储，热存储为空时回退冷归档 `LoadMessagesAsync` + 内存分页。

### SearchConversationsAsync

```csharp
Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
    string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default);
```

**行为**：合并热存储和冷归档搜索结果，去重后按 `LastMessageAt` 降序排列，应用分页。

---

## IConversationRepository（查询扩展）

冷归档仓储接口新增的查询方法：

### ListConversationsAsync

```csharp
Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
    string tenantId, int skip, int take, CancellationToken cancellationToken = default);
```

### SearchConversationsAsync

```csharp
Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
    string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default);
```

---

## ConversationMessage.IdempotencyKey

消息幂等键，用于防止重试导致消息重复写入。相同 `IdempotencyKey` 的消息在 `AppendMessagesAsync` 中会被去重跳过。

```csharp
public string? IdempotencyKey { get; set; }
```

---

## HTTP 端点（Engine Host）

### 用户端点

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/conversations?skip=0&take=20` | 列出会话（自动过滤 `IsDeletedByUser=0`） |
| GET | `/conversations/search?keyword=xxx&skip=0&take=20` | 按标题搜索会话（仅查 `Title` 字段，不碰消息表） |
| GET | `/conversations/{conversationId}` | 获取单个会话 |
| GET | `/conversations/{conversationId}/messages?skip=0&take=50` | 分页获取消息 |
| DELETE | `/conversations/{conversationId}` | 软删除会话（设置 `IsDeletedByUser=1`，数据保留供审计） |

> 注意：`/conversations/search` 必须注册在 `/conversations/{conversationId}` 之前，避免路由冲突。

### 审计端点（独立路由前缀，需管理员角色）

| 方法 | 路径 | 说明 |
|------|------|------|
| GET | `/audit/conversations?skip=0&take=20` | 列出全量会话（含已删除） |
| GET | `/audit/conversations/search?keyword=xxx&startDate=...&endDate=...&skip=0&take=20` | 搜索消息内容（强制时间范围，结果脱敏） |
| GET | `/audit/conversations/{conversationId}` | 获取单个会话详情（含已删除，消息内容脱敏） |

审计端点约束：
- 返回的消息内容需脱敏处理（邮箱、手机号、身份证等 PII 字段）
- 搜索接口强制要求 `startDate` / `endDate` 时间范围参数，利用 `IX_Messages_Tenant_Time` 索引限定扫描范围
- 审计查询可跨 `ConversationMessages` + `ConversationMessagesArchive` 两表 UNION 查询

---

## 调用方使用模式

### 创建新会话

```csharp
var record = new ConversationRecord
{
    ConversationId = conversationId,
    TenantId = tenantId,
    UserId = userId,
    AgentId = agentId,
    TraceId = traceId
};
await store.CreateAsync(record);
```

### 追加消息（含冲突重试）

```csharp
var result = await store.AppendMessagesAsync(tenantId, conversationId, expectedVersion, messages);
if (!result.Success)
{
    // 重新加载记录，重新分配序号，重试一次
    var record = await store.GetRecordAsync(tenantId, conversationId);
    // ... 重新构建 messages 并重试
}
```

### 更新会话状态

```csharp
await store.UpdateStatusAsync(tenantId, conversationId, ConversationStatus.Failed, expectedVersion);
```

## Tests


## 测试策略

会话存储链路的测试围绕接口契约展开，确保所有实现（InMemory / Redis / DualWrite）行为一致。

## 单元测试

### InMemoryConversationStore

| 测试场景 | 验证点 |
|----------|--------|
| 创建新会话 | CreateAsync 返回 true，GetRecordAsync 可读回 |
| 创建重复会话 | CreateAsync 返回 false，原记录不变 |
| 追加消息 | AppendMessagesAsync 返回 Success，Version 自增 |
| 追加消息版本冲突 | expectedVersion 不匹配时返回 Conflict |
| 获取最近 N 条消息 | GetMessagesAsync 按 Sequence 升序返回，不超过 maxMessages |
| 更新状态 | UpdateStatusAsync 成功后 Status 变更 |
| 更新状态版本冲突 | expectedVersion 不匹配时返回 false |
| 不存在的会话 | GetRecordAsync 返回 null |
| 消息序号递增 | 追加后 Sequence 连续递增 |
| 列出会话 | ListConversationsAsync 按租户过滤，按 LastMessageAt 降序，支持分页 |
| 列出会话分页 | skip/take 正确应用 |
| 搜索会话 | SearchConversationsAsync 按关键词匹配消息内容 |
| 搜索大小写不敏感 | SearchConversationsAsync 不区分大小写 |
| 消息分页 | GetMessagesPagedAsync 按 skip/take 返回正确范围 |
| 消息幂等去重 | 相同 IdempotencyKey 的消息被跳过，SkippedDuplicateCount > 0 |

### RedisConversationStore

| 测试场景 | 验证点 |
|----------|--------|
| 序列化与反序列化 | ConversationRecord / ConversationMessage 完整往返（CamelCase 命名策略） |
| TTL 设置 | 写入后 Redis 键带有正确过期时间 |
| Key 格式 | conversation:{tenantId}:{conversationId} |
| CreateAsync NX 语义 | 仅在 key 不存在时写入 |
| Metrics 追踪 | 读写操作记录延迟和命中率指标 |
| 并发写入冲突 | 多线程同时追加时版本冲突正确返回 |
| Redis 不可用 | GetDatabase 返回 null 时优雅降级 |

### DualWriteConversationStore

| 测试场景 | 验证点 |
|----------|--------|
| 热冷双写成功 | Redis 和 SQL Server 均写入成功 |
| 冷归档失败补偿 | SQL Server 写入失败时，Redis 仍成功，记录 Error 日志 |
| 冷归档重试 | SQL Server 临时失败后按 RetryCount + 指数退避重试 |
| 热存储失败 | Redis 写入失败时整体失败，不写冷存储 |
| 冷归档异步执行 | 不阻塞主路径返回 |
| 消息归档补偿 | AppendMessagesAsync 后即发即弃归档消息行，失败不影响热存储 |
| EnableColdArchive=false 跳过归档 | 冷归档禁用时仅走热存储 |
| 列出会话委托 | ListConversationsAsync 委托给热存储 |
| 搜索会话委托 | SearchConversationsAsync 委托给热存储 |
| 消息分页委托 | GetMessagesPagedAsync 委托给热存储 |

### SqlServerConversationRepository

| 测试场景 | 验证点 |
|----------|--------|
| 自动建表 | 首次写入时创建 ConversationRecords、ConversationMessages 表和索引，以及 TVP 类型 |
| 构造函数校验 | 连接字符串为空时抛出 InvalidOperationException |
| MERGE INTO 新记录 | 不存在时 INSERT |
| MERGE INTO 已有记录 | 已存在时 UPDATE |
| 批量消息写入 | TVP 批量插入消息行 |
| 消息加载租户隔离 | LoadMessagesAsync 按 TenantId + ConversationId 过滤 |
| 重试逻辑 | 临时错误时按 RetryCount + 指数退避重试 |
| EnableColdArchive=false | 不执行写入 |

### SqliteConversationRepository

| 测试场景 | 验证点 |
|----------|--------|
| 自动建表 | 首次写入时创建 ConversationRecords、ConversationMessages 表和索引 |
| 构造函数校验 | 连接字符串为空时抛出 InvalidOperationException |
| UPSERT 新记录 | 不存在时 INSERT |
| UPSERT 已有记录 | 已存在时 UPDATE（ON CONFLICT） |
| 批量消息写入 | 事务逐行 INSERT OR IGNORE |
| 消息加载租户隔离 | LoadMessagesAsync 按 TenantId + ConversationId 过滤 |
| 重试逻辑 | 临时错误时按 RetryCount + 指数退避重试 |
| EnableColdArchive=false | 不执行写入 |
| 列出会话 | ListConversationsAsync 按租户过滤，按 LastMessageAt 降序，支持分页 |
| 搜索会话 | SearchConversationsAsync 按关键词匹配消息内容 |

### ConversationQueryService

| 测试场景 | 验证点 |
|----------|--------|
| 列出会话仅热存储 | 无冷归档时直接返回热存储结果 |
| 列出会话热冷合并 | 热 + 冷结果按 ConversationId 去重，热存储版本优先 |
| 列出会话冷归档失败 | 冷归档异常时优雅降级到热存储结果 |
| 获取单个会话热存储命中 | 热存储有记录时直接返回 |
| 获取单个会话冷归档回退 | 热存储无记录时查冷归档，同时加载消息体 |
| 获取单个会话均未找到 | 热存储和冷归档均无记录时返回 null |
| 消息分页热存储有数据 | 直接返回热存储分页结果 |
| 消息分页冷归档回退 | 热存储为空时查冷归档 LoadMessagesAsync + 内存分页 |
| 搜索会话仅热存储 | 无冷归档时直接返回热存储搜索结果 |
| 搜索会话热冷合并 | 热 + 冷搜索结果按 ConversationId 去重 |
| 搜索会话冷归档失败 | 冷归档异常时优雅降级到热存储结果 |

### ConversationStoreMetrics

| 测试场景 | 验证点 |
|----------|--------|
| 计数器递增 | Hit/Miss/ReadFailure/WriteFailure 正确递增 |
| 线程安全 | 并发调用计数器不丢失 |
| LogSnapshot | 输出包含所有指标值 |

## 集成测试

| 测试场景 | 验证点 |
|----------|--------|
| 完整读写周期 | 创建 → 追加 → 读取 → 状态更新 → 验证一致性 |
| 流式取消写回 | 取消后 partial assistant 消息落库，状态为 Cancelled |
| 流式失败写回 | 失败后 partial assistant 消息落库，状态为 Failed |
| 非流式成功写回 | 最终 assistant 消息落库 |
| 版本冲突重试 | 冲突后重新加载并重试追加成功 |
| 未知角色消息 | 跳过不回放，记录告警日志 |

## 验收口径

判断会话存储是否满足执行侧要求：

- [ ] conversationId 能稳定透传到 Core
- [ ] 历史消息能按 Sequence 顺序回放
- [ ] 未知角色不会污染执行链路
- [ ] 非流式和流式都能写回 user / assistant / tool
- [ ] 取消和失败状态能正确落库
- [ ] Redis / 双写场景下版本冲突可重试
- [ ] 会话列表查询按租户隔离，支持分页
- [ ] 会话搜索按关键词匹配消息内容，不区分大小写
- [ ] 消息分页查询支持 skip/take
- [ ] 热/冷合并查询正确去重，热存储版本优先
- [ ] 冷归档不可用时查询优雅降级
- [ ] 消息幂等去重（IdempotencyKey）防止重复写入

## Conventions


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
