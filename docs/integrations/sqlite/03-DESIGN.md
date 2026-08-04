# SQLite — 设计文档

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
