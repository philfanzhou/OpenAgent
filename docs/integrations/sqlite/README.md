
## Feature


## 核心能力

Agent.Core 使用 SQLite 作为会话冷归档存储，提供轻量级持久化能力。通过 `DualWriteConversationStore` 实现双写（Redis 热存储 + SQLite 冷归档）。

## 关键接口与类

| 接口/类 | 所在文件 | 职责 |
|---------|----------|------|
| `SqliteConversationRepository` | `src/Core/Conversation/Repository/SqliteConversationRepository.cs` | SQLite 会话归档实现 |
| `DualWriteConversationStore` | `src/Core/Conversation/Store/DualWriteConversationStore.cs` | 双写存储（Redis + 冷归档） |
| `ConversationStoreOptions` | `Agent.Contracts/Conversation/ConversationStoreOptions.cs` | 存储配置选项（ColdArchiveProvider=Sqlite） |

## 功能范围

- 会话记录冷归档（`ArchiveAsync`）+ 消息行级归档（`ArchiveMessagesAsync`）
- 自动建表（`ConversationRecords` + `ConversationMessages`）
- INSERT ON CONFLICT 实现 Upsert（幂等写入）
- 事务逐行 INSERT OR IGNORE 批量消息写入
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

## 适用场景

- 开发/测试环境（无需 SQL Server）
- 单机部署（嵌入式数据库）
- 轻量级持久化需求

## Specification


## 表结构

### ConversationRecords

会话元数据表（以 `ConversationId` 为主键）：

```sql
CREATE TABLE IF NOT EXISTS ConversationRecords (
    ConversationId TEXT NOT NULL PRIMARY KEY,
    TenantId       TEXT NOT NULL,
    UserId         TEXT NOT NULL,
    AgentId        TEXT,
    TraceId        TEXT,
    Version        INTEGER NOT NULL DEFAULT 1,
    Status         INTEGER NOT NULL DEFAULT 0,
    CreatedAt      TEXT NOT NULL,
    UpdatedAt      TEXT NOT NULL,
    LastMessageAt  TEXT NOT NULL,
    MessageCount   INTEGER NOT NULL DEFAULT 0
);

CREATE INDEX IF NOT EXISTS IX_Records_Tenant_User ON ConversationRecords (TenantId, UserId, UpdatedAt);
CREATE INDEX IF NOT EXISTS IX_Records_Tenant_Agent ON ConversationRecords (TenantId, AgentId, UpdatedAt);
```

### ConversationMessages

消息行级表（以 `ConversationId` + `Sequence` 为复合主键）：

```sql
CREATE TABLE IF NOT EXISTS ConversationMessages (
    ConversationId TEXT NOT NULL,
    Sequence       INTEGER NOT NULL,
    MessageId      TEXT NOT NULL,
    Role           TEXT NOT NULL,
    Content        TEXT NOT NULL,
    ToolCallId     TEXT,
    ToolName       TEXT,
    Timestamp      TEXT NOT NULL,
    MetadataJson   TEXT,
    TenantId       TEXT NOT NULL,
    PRIMARY KEY (ConversationId, Sequence)
);

CREATE INDEX IF NOT EXISTS IX_Messages_Tenant_Time ON ConversationMessages (TenantId, Timestamp);
```

### 与 SQL Server 的差异

| 特性 | SQLite | SQL Server |
|------|--------|------------|
| 日期时间类型 | TEXT（ISO 8601 字符串） | DATETIMEOFFSET |
| 批量消息写入 | 事务逐行 INSERT OR IGNORE | TVP MERGE INTO |
| UPSERT 语法 | INSERT ON CONFLICT DO UPDATE | MERGE INTO |
| 建表语法 | CREATE TABLE IF NOT EXISTS | IF NOT EXISTS (sys.tables 检查) |

## UPSERT 逻辑

### 会话记录：INSERT ON CONFLICT

```sql
INSERT INTO ConversationRecords
    (ConversationId, TenantId, UserId, AgentId, TraceId,
     Version, Status, CreatedAt, UpdatedAt, LastMessageAt, MessageCount)
VALUES
    ($ConversationId, $TenantId, $UserId, $AgentId, $TraceId,
     $Version, $Status, $CreatedAt, $UpdatedAt, $LastMessageAt, $MessageCount)
ON CONFLICT(ConversationId) DO UPDATE SET
    AgentId = excluded.AgentId,
    TraceId = excluded.TraceId,
    Version = excluded.Version,
    Status = excluded.Status,
    UpdatedAt = excluded.UpdatedAt,
    LastMessageAt = excluded.LastMessageAt,
    MessageCount = excluded.MessageCount;
```

### 消息行：事务逐行 INSERT OR IGNORE

```sql
-- 在事务中逐行执行：
INSERT OR IGNORE INTO ConversationMessages
    (ConversationId, Sequence, MessageId, Role, Content,
     ToolCallId, ToolName, Timestamp, MetadataJson, TenantId)
VALUES
    ($ConversationId, $Sequence, $MessageId, $Role, $Content,
     $ToolCallId, $ToolName, $Timestamp, $MetadataJson, $TenantId);
```

## 配置选项

SQLite 通过 `ColdArchiveProvider = "Sqlite"` 启用，使用与 SQL Server 相同的 `ColdArchiveConnectionString` 配置（连接字符串指向 SQLite 文件路径）。

## Design


## SqliteConversationRepository 设计

```
SqliteConversationRepository : IConversationRepository (实现 IConversationRepository : IDisposable)
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
  │     │     └── 事务逐行 INSERT OR IGNORE
  │     ├── RecordColdArchiveSuccess()
  │     └── 异常 → RecordColdArchiveFailure()
  │
  ├── LoadMessagesAsync(tenantId, conversationId)
  │     ├── SELECT FROM ConversationMessages
  │     │     WHERE TenantId = $TenantId AND ConversationId = $ConversationId
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
  │     ├── CREATE INDEX IF NOT EXISTS
  │     └── _initialized = true
  │
  └── RetryAsync(action)
        ├── for attempt = 0 to ColdArchiveRetryCount
        │     ├── action()
        │     ├── 成功 → return
        │     └── 异常 → Log Warning, Delay(delayMs), delayMs *= 2
        └── 重试耗尽后异常向上传播
```

## 批量消息写入

与 SQL Server 使用 TVP 不同，SQLite 使用事务包裹的逐行 INSERT OR IGNORE：

```
BulkInsertMessagesAsync(tenantId, conversationId, messages)
  ├── new SqliteConnection(_connectionString)
  ├── BeginTransaction()
  ├── 对每条消息：
  │     └── cmd.ExecuteNonQueryAsync() — INSERT OR IGNORE
  └── CommitAsync()
```

- `INSERT OR IGNORE` 在主键冲突时跳过（消息去重）
- 事务保证原子性

## 日期时间处理

SQLite 不原生支持 `DateTimeOffset`，使用 ISO 8601 字符串存储：
- 写入：`record.CreatedAt.ToString("O")` → `"2026-06-16T10:30:00.0000000+00:00"`
- 读取：`DateTimeOffset.Parse(reader.GetString(...))`

## Tasks


## 已完成

- [x] `SqliteConversationRepository` 实现（IConversationRepository 接口）
- [x] 自动建表（`ConversationRecords` + `ConversationMessages` + 索引）
- [x] INSERT ON CONFLICT Upsert
- [x] 事务逐行 INSERT OR IGNORE 批量消息写入
- [x] 租户隔离（`LoadMessagesAsync` 强制 `TenantId` 过滤）
- [x] 指数退避重试（`ColdArchiveRetryCount` / `ColdArchiveRetryDelayMs`）
- [x] `EnableColdArchive` 配置开关
- [x] `ColdArchiveConnectionString` 配置
- [x] `ColdArchiveProvider = "Sqlite"` 配置
- [x] .NET 8 Keyed DI 注册
- [x] 性能指标收集（`ConversationStoreMetrics`）

## 待办

- [ ] 冷归档补偿机制（自动重试失败的归档）
- [ ] 从冷归档恢复到热存储的流程
- [ ] SQLite 并发写入优化（WAL 模式）
- [ ] 连接池优化（SQLite 单写锁）
- [ ] **ConversationRecords 表新增字段**：Title、IsDeletedByUser、DeletedAt、ArchivedAt
- [ ] **软删除实现**：SoftDeleteAsync 方法 + 用户侧查询自动过滤 IsDeletedByUser=0
- [ ] **会话标题生成**：首轮消息截取初始标题 + 异步 LLM 摘要更新
- [ ] **数据保留策略**：SQLite 场景数据量较小，可考虑简化方案（如定期 VACUUM + 按需清理归档表）

## Tests


## 单元测试

### SqliteConversationRepository

- `ArchiveAsync` EnableColdArchive 为 false 时跳过
- `ArchiveAsync` 正确执行 INSERT ON CONFLICT Upsert
- `ArchiveMessagesAsync` 事务逐行 INSERT OR IGNORE
- `LoadMessagesAsync` 按 TenantId + ConversationId 加载
- `GetRecordAsync` 加载记录 + 完整消息列表
- `EnsureInitializedAsync` 首次调用时创建表和索引
- `EnsureInitializedAsync` 后续调用跳过建表
- `UpsertRecordAsync` 新记录插入
- `UpsertRecordAsync` 已有记录更新（ON CONFLICT）
- `BulkInsertMessagesAsync` 事务原子性
- `BulkInsertMessagesAsync` INSERT OR IGNORE 消息去重
- `RetryAsync` 首次成功不重试
- `RetryAsync` 失败后指数退避重试
- `RetryAsync` 重试耗尽后异常向上传播
- `ColdArchiveConnectionString` 为空时构造函数抛出异常

### DualWriteConversationStore

- `CreateAsync` 热存储成功后触发冷归档
- `CreateAsync` 热存储失败时跳过冷归档
- `AppendMessagesAsync` 热存储成功后触发消息冷归档
- `AppendMessagesAsync` 热存储失败时返回失败结果
- `UpdateStatusAsync` 热存储成功后触发冷归档
- `GetRecordAsync` 仅从热存储读取
- 冷归档异常不阻塞主流程
- EnableColdArchive 为 false 时不触发冷归档

## 集成测试

- SQLite 文件数据库创建与连接验证
- 自动建表验证
- INSERT ON CONFLICT 幂等性验证
- 批量消息事务写入验证
- DualWrite 端到端流程

## Conventions


## 表结构

- 元数据表：`ConversationRecords`，主键 `ConversationId`
- 消息表：`ConversationMessages`，复合主键 `(ConversationId, Sequence)`
- 自动建表：首次归档时检查并创建（`CREATE TABLE IF NOT EXISTS`）
- 索引：
  - `IX_Records_Tenant_User`（TenantId, UserId, UpdatedAt）
  - `IX_Records_Tenant_Agent`（TenantId, AgentId, UpdatedAt）
  - `IX_Messages_Tenant_Time`（TenantId, Timestamp）

## 连接字符串

- 从 `ConversationStoreOptions.ColdArchiveConnectionString` 读取
- 为空时构造函数抛出 `InvalidOperationException`
- 使用 `Data Source=path/to/file.db` 格式
- 建议使用 `Microsoft.Data.Sqlite` NuGet 包

## 重试策略

- 写入失败时采用指数退避重试
- 初始延迟：`ColdArchiveRetryDelayMs`（默认 1000ms）
- 每次重试延迟翻倍：`delayMs *= 2`
- 最多重试：`ColdArchiveRetryCount`（默认 3 次）
- 重试仍失败时异常向上传播，由 `ArchiveWithCompensationAsync` 捕获
- 重试事件记录 Warning 日志（当前次数、延迟）

## 写入模式

- 仅在 `DualWrite` 模式下写入 SQLite
- 会话记录：INSERT ON CONFLICT DO UPDATE
- 消息行：事务逐行 INSERT OR IGNORE
- 写入为异步 Fire-and-Forget
- 不阻塞 Redis 主写入
- 租户隔离：所有查询强制 `WHERE TenantId = $TenantId`

## 日期时间

- 所有时间字段以 ISO 8601 字符串存储（`DateTimeOffset.ToString("O")`）
- 读取时通过 `DateTimeOffset.Parse()` 还原
- 精度与 `DateTimeOffset` 一致（含时区信息）

## 数据一致性

- 热存储（Redis）为权威数据源
- 冷归档为异步写入，可能短暂落后
- 冷归档失败时热存储数据一致，冷存储需要补偿
- 补偿日志明确标记："Hot store is consistent. Cold store needs compensation."

## 消息序列化

- 消息以行级方式存储在 `ConversationMessages` 表
- 每条消息的 `Metadata` 字段独立 JSON 序列化到 `MetadataJson` 列
- 使用 `System.Text.Json`，CamelCase 命名策略

## 性能指标

- 归档成功：`RecordColdArchiveSuccess()`
- 归档失败：`RecordColdArchiveFailure()`
- 归档延迟：`RecordColdArchiveLatency(ms)`
- 使用 `Stopwatch` 计时
