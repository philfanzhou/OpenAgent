# SQL Server — 设计文档

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
