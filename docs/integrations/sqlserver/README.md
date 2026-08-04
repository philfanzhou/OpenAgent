
## Feature


## 核心能力

Agent.Core 使用 SQL Server 作为会话冷归档存储，提供长期持久化能力。通过 `DualWriteConversationStore` 实现双写（Redis 热存储 + SQL Server 冷归档）。

## 关键接口与类

| 接口/类 | 所在文件 | 职责 |
|---------|----------|------|
| `SqlServerConversationRepository` | `src/Core/Conversation/Repository/SqlServerConversationRepository.cs` | SQL Server 会话归档实现 |
| `DualWriteConversationStore` | `src/Core/Conversation/Store/DualWriteConversationStore.cs` | 双写存储（Redis + SQL Server） |
| `ConversationStoreOptions` | `Agent.Contracts/Conversation/ConversationStoreOptions.cs` | 存储配置选项 |

## 功能范围

- 会话记录冷归档（`ArchiveAsync`）+ 消息行级归档（`ArchiveMessagesAsync`）
- 自动建表（`ConversationRecords` + `ConversationMessages`）
- MERGE INTO 实现 Upsert（幂等写入）
- TVP（Table-Valued Parameter）批量消息插入
- 指数退避重试
- 通过 `DualWriteConversationStore` 实现双写
- 归档为异步 Fire-and-Forget，不阻塞主流程

## DualWrite 架构

```
DualWriteConversationStore : IConversationStore
  ├── IConversationStore (RedisConversationStore) — 热存储（主）
  └── IConversationRepository — 冷归档（从）
```

- 读取：仅从热存储读取
- 写入：先写热存储，成功后异步写冷归档
- 冷归档失败不影响主流程

## Specification


## 表结构

### ConversationRecords

会话元数据表（以 `ConversationId` 为主键）：

```sql
CREATE TABLE ConversationRecords (
    ConversationId  NVARCHAR(128) NOT NULL PRIMARY KEY,
    TenantId        NVARCHAR(128) NOT NULL,
    UserId          NVARCHAR(128) NOT NULL,
    AgentId         NVARCHAR(128) NULL,
    TraceId         NVARCHAR(128) NULL,
    Version         INT NOT NULL DEFAULT 1,
    Status          INT NOT NULL DEFAULT 0,
    CreatedAt       DATETIMEOFFSET NOT NULL,
    UpdatedAt       DATETIMEOFFSET NOT NULL,
    LastMessageAt   DATETIMEOFFSET NOT NULL,
    MessageCount    INT NOT NULL DEFAULT 0,
    Title           NVARCHAR(256) NULL,              -- 会话标题
    IsDeletedByUser BIT NOT NULL DEFAULT 0,          -- 用户软删除标记
    DeletedAt       DATETIMEOFFSET NULL,             -- 用户删除时间
    ArchivedAt      DATETIMEOFFSET NOT NULL           -- 归档入库时间
);

CREATE INDEX IX_ConversationRecords_Tenant_User ON ConversationRecords (TenantId, UserId, UpdatedAt);
CREATE INDEX IX_ConversationRecords_Tenant_Agent ON ConversationRecords (TenantId, AgentId, UpdatedAt);
CREATE INDEX IX_ConversationRecords_Tenant_Deleted ON ConversationRecords (TenantId, IsDeletedByUser, LastMessageAt);
CREATE INDEX IX_ConversationRecords_ArchivedAt ON ConversationRecords (ArchivedAt);
```

### ConversationMessages

消息行级表（以 `ConversationId` + `Sequence` 为复合主键）：

```sql
CREATE TABLE ConversationMessages (
    ConversationId NVARCHAR(128) NOT NULL,
    Sequence       INT NOT NULL,
    MessageId      NVARCHAR(128) NOT NULL,
    Role           NVARCHAR(16) NOT NULL,
    Content        NVARCHAR(MAX) NOT NULL,
    ToolCallId     NVARCHAR(128) NULL,
    ToolName       NVARCHAR(128) NULL,
    Timestamp      DATETIMEOFFSET NOT NULL,
    MetadataJson   NVARCHAR(MAX) NULL,
    TenantId       NVARCHAR(128) NOT NULL,

    PRIMARY KEY (ConversationId, Sequence)
);

CREATE INDEX IX_Messages_Tenant_Time ON ConversationMessages (TenantId, Timestamp);
```

### TVP 类型

用于批量消息插入的表值参数类型：

```sql
CREATE TYPE dbo.ConversationMessageType AS TABLE (
    ConversationId NVARCHAR(128),
    Sequence       INT,
    MessageId      NVARCHAR(128),
    Role           NVARCHAR(16),
    Content        NVARCHAR(MAX),
    ToolCallId     NVARCHAR(128),
    ToolName       NVARCHAR(128),
    Timestamp      DATETIMEOFFSET,
    MetadataJson   NVARCHAR(MAX)
);
```

## UPSERT 逻辑

### 会话记录：MERGE INTO

```sql
MERGE INTO ConversationRecords AS target
USING (VALUES (@ConversationId, @TenantId, ...)) AS source (...)
ON target.ConversationId = source.ConversationId
WHEN MATCHED THEN
    UPDATE SET AgentId = source.AgentId, TraceId = source.TraceId, ...
WHEN NOT MATCHED THEN
    INSERT (ConversationId, TenantId, ...) VALUES (source.ConversationId, source.TenantId, ...);
```

### 消息行：MERGE INTO + TVP

```sql
MERGE INTO ConversationMessages AS target
USING @messages AS source
ON target.ConversationId = source.ConversationId AND target.Sequence = source.Sequence
WHEN NOT MATCHED THEN
    INSERT (ConversationId, Sequence, MessageId, Role, Content,
            ToolCallId, ToolName, Timestamp, MetadataJson, TenantId)
    VALUES (source.ConversationId, source.Sequence, source.MessageId, source.Role, source.Content,
            source.ToolCallId, source.ToolName, source.Timestamp, source.MetadataJson, @TenantId);
```

## 配置选项

```csharp
// ConversationStoreOptions 中与冷归档相关的配置
public bool EnableColdArchive { get; set; } = true;            // 是否启用冷归档
public string? ColdArchiveConnectionString { get; set; }       // 连接字符串
public string ColdArchiveProvider { get; set; } = "SqlServer"; // 冷归档提供器
public int ColdArchiveRetryCount { get; set; } = 3;            // 重试次数
public int ColdArchiveRetryDelayMs { get; set; } = 1000;       // 重试初始延迟（ms）

// 数据分层管理配置
public int MessageRetentionDays { get; set; } = 90;              // 消息活跃期保留天数
public int ArchiveMigrationIntervalMinutes { get; set; } = 60;   // 迁移任务间隔（分钟）
public int ArchiveMigrationBatchSize { get; set; } = 100;        // 每批迁移会话数

// 会话标题配置
public int TitleTruncateLength { get; set; } = 50;               // 标题截取字符数
public bool EnableTitleSummarization { get; set; } = true;       // 是否启用 LLM 摘要标题
```

## 归档表（ConversationMessagesArchive）

超期消息从 `ConversationMessages` 迁移至归档表，表结构完全一致：

```sql
CREATE TABLE ConversationMessagesArchive (
    ConversationId NVARCHAR(128) NOT NULL,
    Sequence       INT NOT NULL,
    MessageId      NVARCHAR(128) NOT NULL,
    Role           NVARCHAR(16) NOT NULL,
    Content        NVARCHAR(MAX) NOT NULL,
    ToolCallId     NVARCHAR(128) NULL,
    ToolName       NVARCHAR(128) NULL,
    Timestamp      DATETIMEOFFSET NOT NULL,
    MetadataJson   NVARCHAR(MAX) NULL,
    TenantId       NVARCHAR(128) NOT NULL,

    PRIMARY KEY (ConversationId, Sequence)
) WITH (DATA_COMPRESSION = PAGE);

CREATE INDEX IX_ArchiveMessages_Tenant_Time ON ConversationMessagesArchive (TenantId, Timestamp);
CREATE INDEX IX_ArchiveMessages_ConversationId ON ConversationMessagesArchive (ConversationId);
```

- 启用页压缩（Page Compression），空间占用降低 50-70%
- 只写不读（审计调取时查询）
- 审计搜索跨表查询：`UNION ALL ConversationMessages + ConversationMessagesArchive`

## 数据分层迁移

后台 IHostedService 定时任务，按 `ArchiveMigrationIntervalMinutes` 间隔执行：

```sql
-- 1. 扫描超期会话（ArchivedAt < now - MessageRetentionDays）
SELECT TOP (@BatchSize) ConversationId
FROM ConversationRecords
WHERE ArchivedAt < DATEADD(DAY, -@RetentionDays, SYSUTCDATETIME())
  AND EXISTS (SELECT 1 FROM ConversationMessages WHERE ConversationId = ConversationRecords.ConversationId)

-- 2. 同事务内迁移每个会话的消息
BEGIN TRAN
  INSERT INTO ConversationMessagesArchive SELECT * FROM ConversationMessages WHERE ConversationId = @id
  DELETE FROM ConversationMessages WHERE ConversationId = @id
COMMIT
```

## 消息存储

- 消息以行级方式存储在 `ConversationMessages` 表中（非 JSON blob）
- `Metadata` 字段独立 JSON 序列化到 `MetadataJson` 列
- 批量写入通过 TVP `dbo.ConversationMessageType` 实现
- 使用 `System.Text.Json` 进行 Metadata 序列化

## Design


## DualWriteConversationStore 设计

```
DualWriteConversationStore : IConversationStore, IDisposable
  ├── IConversationStore _hotStore (RedisConversationStore)
  ├── IConversationRepository _coldArchive
  ├── ConversationStoreOptions _options
  └── ILogger<DualWriteConversationStore> _logger
```

DualWrite 通过 `IConversationRepository` 接口注入冷归档实例，具体实现由 .NET 8 Keyed DI 根据 `ColdArchiveProvider` 配置动态选取。

## 写入流程

### CreateAsync

```
DualWrite.CreateAsync(record)
  ├── 1. _hotStore.CreateAsync(record) → hotResult
  │     └── 失败 → 跳过冷归档，return false
  └── 2. EnableColdArchive → ArchiveWithCompensationAsync(record)
        └── _coldArchive.ArchiveAsync(record)  — Fire-and-Forget（不 await）
              └── 异常 → Log Error（热存储一致，冷存储需补偿）
```

### AppendMessagesAsync

```
DualWrite.AppendMessagesAsync(tenantId, conversationId, expectedVersion, messages)
  ├── 1. _hotStore.AppendMessagesAsync(...) → result
  │     └── 失败 → return result
  └── 2. EnableColdArchive && 成功
        ├── _coldArchive.ArchiveMessagesAsync(tenantId, conversationId, messages)  — Fire-and-Forget
        └── 异常 → Log Error
```

### UpdateStatusAsync

```
DualWrite.UpdateStatusAsync(tenantId, conversationId, status, expectedVersion)
  ├── 1. _hotStore.UpdateStatusAsync(...) → result
  └── 2. EnableColdArchive && 成功
        ├── _hotStore.GetRecordAsync(...) → record
        └── ArchiveWithCompensationAsync(record)  — Fire-and-Forget
```

## 读取流程

```
DualWrite.GetRecordAsync(tenantId, conversationId)
  └── _hotStore.GetRecordAsync(tenantId, conversationId)  — 仅从热存储读取

DualWrite.GetMessagesAsync(tenantId, conversationId, maxMessages)
  └── _hotStore.GetMessagesAsync(tenantId, conversationId, maxMessages)  — 仅从热存储读取
```

## SqlServerConversationRepository 设计

```
SqlServerConversationRepository : IConversationRepository (实现 IConversationRepository : IDisposable)
  ├── 依赖：IOptions<ConversationStoreOptions>, ILogger, ConversationStoreMetrics
  ├── _connectionString → ColdArchiveConnectionString
  ├── _initialized → 自动建表标志
  │
  ├── ArchiveAsync(record)
  │     ├── EnableColdArchive == false → return
  │     ├── EnsureInitializedAsync() → 自动建表
  │     ├── RetryAsync(() => UpsertRecordAsync(record))
  │     ├── RecordColdArchiveSuccess()
  │     └── 异常 → RecordColdArchiveFailure()
  │
  ├── ArchiveMessagesAsync(tenantId, conversationId, messages)
  │     ├── EnableColdArchive == false → return
  │     ├── EnsureInitializedAsync()
  │     ├── RetryAsync(() => BulkInsertMessagesAsync(...))
  │     │     └── TVP 批量 MERGE INTO ConversationMessages
  │     ├── RecordColdArchiveSuccess()
  │     └── 异常 → RecordColdArchiveFailure()
  │
  ├── LoadMessagesAsync(tenantId, conversationId)
  │     ├── SELECT FROM ConversationMessages
  │     │     WHERE TenantId = @TenantId AND ConversationId = @ConversationId
  │     └── 返回 IReadOnlyList<ConversationMessage>
  │
  ├── GetRecordAsync(tenantId, conversationId)
  │     ├── SELECT FROM ConversationRecords WHERE TenantId + ConversationId
  │     ├── LoadMessagesAsync(tenantId, conversationId) → 加载消息行
  │     └── 返回 ConversationRecord（含完整消息列表）
  │
  ├── EnsureInitializedAsync()
  │     ├── _initialized == true → return
  │     ├── CREATE TABLE IF NOT EXISTS ConversationRecords (...)
  │     ├── CREATE TABLE IF NOT EXISTS ConversationMessages (...)
  │     ├── CREATE TYPE IF NOT EXISTS ConversationMessageType (TVP)
  │     ├── 创建索引
  │     └── _initialized = true
  │
  └── RetryAsync(action)
        ├── for attempt = 0 to ColdArchiveRetryCount
        │     ├── action()
        │     ├── 成功 → return
        │     └── 异常 → Log Warning, Delay(delayMs), delayMs *= 2
        └── 重试耗尽后异常向上传播
```

## ArchiveWithCompensationAsync

```
ArchiveWithCompensationAsync(record, ct)
  ├── try { await _coldArchive.ArchiveAsync(record, ct); }
  └── catch (Exception ex)
        └── Log Error: "Hot store is consistent. Cold store needs compensation."
```

- 冷归档失败时仅记录日志，不阻塞主流程
- 日志明确指出热存储一致，冷存储需要补偿

## Tasks


## 已完成

- [x] `SqlServerConversationRepository` 实现（IConversationRepository 接口）
- [x] 自动建表（`ConversationRecords` + `ConversationMessages` + TVP 类型 + 索引）
- [x] `DualWriteConversationStore` 双写模式实现
- [x] 读取仅从热存储（Redis）
- [x] 写入先热后冷（Fire-and-Forget）
- [x] 冷归档失败补偿日志
- [x] 行级消息表（`ConversationMessages`）+ TVP 批量写入
- [x] 租户隔离（`LoadMessagesAsync` 强制 `TenantId` 过滤）
- [x] 指数退避重试（`ColdArchiveRetryCount` / `ColdArchiveRetryDelayMs`）
- [x] `ColdArchiveProvider` 配置 + .NET 8 Keyed DI 选择
- [x] `EnableColdArchive` 配置开关
- [x] `ColdArchiveConnectionString` 配置
- [x] 性能指标收集（`ConversationStoreMetrics`）

## 待办

- [ ] 冷归档补偿机制（自动重试失败的归档）
- [ ] 从冷归档恢复到热存储的流程
- [ ] 大批量消息归档性能优化
- [ ] 索引优化建议（按查询模式调整）
- [ ] **ConversationRecords 表新增字段**：Title、IsDeletedByUser、DeletedAt、ArchivedAt + 对应索引
- [ ] **ConversationMessagesArchive 归档表建表**：与 ConversationMessages 结构一致，启用页压缩
- [ ] **软删除实现**：SoftDeleteAsync 方法 + 用户侧查询自动过滤 IsDeletedByUser=0
- [ ] **会话标题生成**：首轮消息截取初始标题 + 异步 LLM 摘要更新（失败告警不阻塞）
- [ ] **数据分层迁移任务**：IHostedService 定时扫描超期会话，同事务迁移消息到归档表
- [ ] **审计端点**：独立 /audit/conversations 路由，含已删除会话，消息内容脱敏，搜索强制时间范围
- [ ] **审计搜索跨表查询**：UNION ConversationMessages + ConversationMessagesArchive

## Tests


## 单元测试

### SqlServerConversationRepository

- `ArchiveAsync` EnableColdArchive 为 false 时跳过
- `ArchiveAsync` 正确执行 MERGE INTO Upsert
- `ArchiveMessagesAsync` TVP 批量写入消息行
- `LoadMessagesAsync` 按 TenantId + ConversationId 加载
- `GetRecordAsync` 加载记录 + 完整消息列表
- `EnsureInitializedAsync` 首次调用时创建表、索引和 TVP 类型
- `EnsureInitializedAsync` 后续调用跳过建表
- `UpsertRecordAsync` 新记录插入
- `UpsertRecordAsync` 已有记录更新
- `BulkInsertMessagesAsync` MERGE INTO + TVP 实现
- `RetryAsync` 首次成功不重试
- `RetryAsync` 失败后指数退避重试
- `RetryAsync` 重试耗尽后异常向上传播
- `ColdArchiveConnectionString` 为空时构造函数抛出异常

### DualWriteConversationStore

- `CreateAsync` 热存储成功后触发冷归档
- `CreateAsync` 热存储失败时跳过冷归档
- `AppendMessagesAsync` 热存储成功后触发冷归档
- `AppendMessagesAsync` 热存储失败时返回失败结果
- `UpdateStatusAsync` 热存储成功后触发冷归档
- `UpdateStatusAsync` 热存储失败时返回 false
- `GetRecordAsync` 仅从热存储读取
- `GetMessagesAsync` 仅从热存储读取
- 冷归档异常不阻塞主流程
- EnableColdArchive 为 false 时不触发冷归档

## 集成测试

- SQL Server 连接与断线场景
- 自动建表验证（ConversationRecords + ConversationMessages + TVP）
- MERGE INTO 幂等性验证
- TVP 批量消息写入性能
- DualWrite 端到端流程

## Conventions


## 表结构

- 元数据表：`ConversationRecords`，主键 `ConversationId`
- 消息表：`ConversationMessages`，复合主键 `(ConversationId, Sequence)`
- TVP 类型：`dbo.ConversationMessageType`（用于批量消息插入）
- 自动建表：首次归档时检查并创建（`IF NOT EXISTS`）
- 索引：
  - `IX_ConversationRecords_Tenant_User`（TenantId, UserId, UpdatedAt）
  - `IX_ConversationRecords_Tenant_Agent`（TenantId, AgentId, UpdatedAt）
  - `IX_Messages_Tenant_Time`（TenantId, Timestamp）

## 连接字符串

- 从 `ConversationStoreOptions.ColdArchiveConnectionString` 读取
- 为空时构造函数抛出 `InvalidOperationException`
- 推荐使用 ADO.NET 连接池
- 连接超时由连接字符串配置

## 重试策略

- SQL Server 写入失败时采用指数退避重试
- 初始延迟：`ColdArchiveRetryDelayMs`（默认 1000ms）
- 每次重试延迟翻倍：`delayMs *= 2`
- 最多重试：`ColdArchiveRetryCount`（默认 3 次）
- 重试仍失败时异常向上传播，由 `ArchiveWithCompensationAsync` 捕获
- 重试事件记录 Warning 日志（当前次数、延迟）

## 写入模式

- 仅在 `DualWrite` 模式下写入 SQL Server
- 会话记录：MERGE INTO `ConversationRecords`
- 消息行：MERGE INTO `ConversationMessages` 通过 TVP 批量写入
- 写入为异步 Fire-and-Forget（`_ = ArchiveWithCompensationAsync(...)`）
- 不阻塞 Redis 主写入
- MERGE INTO 保证幂等性，支持重复写入
- 租户隔离：所有查询强制 `WHERE TenantId = @TenantId`

## 数据一致性

- 热存储（Redis）为权威数据源
- 冷归档为异步写入，可能短暂落后
- 冷归档失败时热存储数据一致，冷存储需要补偿
- 补偿日志明确标记："Hot store is consistent. Cold store needs compensation."

## 消息序列化

- 消息以行级方式存储在 `ConversationMessages` 表
- 每条消息的 `Metadata` 字段独立 JSON 序列化到 `MetadataJson` 列
- 使用 `System.Text.Json`，CamelCase 命名策略
- `WriteIndented = false`

## 性能指标

- 归档成功：`RecordColdArchiveSuccess()`
- 归档失败：`RecordColdArchiveFailure()`
- 归档延迟：`RecordColdArchiveLatency(ms)`
- 使用 `Stopwatch` 计时
