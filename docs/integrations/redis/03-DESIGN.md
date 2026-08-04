# Redis — 设计文档

## IConversationStore → RedisConversationStore

```
IConversationStore
  └── RedisConversationStore : IConversationStore
        ├── 依赖：IConnectionMultiplexer, IOptions<ConversationStoreOptions>, ILogger, ConversationStoreMetrics
        ├── GetMessagesAsync(tenantId, conversationId, maxMessages)
        ├── GetRecordAsync(tenantId, conversationId)
        ├── CreateAsync(record)
        ├── AppendMessagesAsync(tenantId, conversationId, expectedVersion, messages)
        └── UpdateStatusAsync(tenantId, conversationId, status, expectedVersion)
```

## Key 构建

```csharp
private static string BuildKey(string tenantId, string conversationId)
    => $"conversation:{tenantId}:{conversationId}";
```

## 读取流程

```
GetRecordAsync(tenantId, conversationId)
  ├── GetDatabase() → IDatabase
  ├── BuildKey(tenantId, conversationId) → key
  ├── db.StringGetAsync(key) → json
  ├── json.IsNullOrEmpty → return null
  └── DeserializeRecord(json) → ConversationRecord

GetMessagesAsync(tenantId, conversationId, maxMessages)
  ├── GetRecordInternalAsync(tenantId, conversationId) → record
  ├── record == null → return Empty, RecordMiss()
  ├── RecordHit()
  ├── record.Messages.TakeLast(maxMessages)
  └── RecordMessagesLoaded(count)
```

## 创建流程

```
CreateAsync(record)
  ├── GetDatabase() → db
  ├── BuildKey(record.TenantId, record.ConversationId) → key
  ├── SerializeRecord(record) → json
  ├── db.StringSetAsync(key, json, TTL, When.NotExists) → created
  │     ├── created == true → Log Info
  │     └── created == false → key 已存在
  └── return created
```

## 追加消息流程

```
AppendMessagesAsync(tenantId, conversationId, expectedVersion, messages)
  ├── GetDatabase() → db
  ├── GetRecordInternalAsync() → record
  ├── record == null → return Conflict("Conversation not found")
  ├── record.Version != expectedVersion
  │     └── return Conflict($"Version conflict: expected {expectedVersion}, actual {record.Version}")
  ├── record.Messages.AddRange(messages)
  ├── record.Version++
  ├── record.MessageCount = record.Messages.Count
  ├── record.UpdatedAt = DateTimeOffset.UtcNow
  ├── record.LastMessageAt = messages[^1].Timestamp
  ├── SerializeRecord(record) → json
  ├── db.StringSetAsync(key, json, TTL) → 覆盖写入
  ├── RecordMessagesWritten(count)
  └── return AppendResult.Ok(record.Version, record.MessageCount)
```

## 更新状态流程

```
UpdateStatusAsync(tenantId, conversationId, status, expectedVersion)
  ├── GetDatabase() → db
  ├── GetRecordInternalAsync() → record
  ├── record == null → return false
  ├── record.Version != expectedVersion → return false
  ├── record.Status = status
  ├── record.Version++
  ├── record.UpdatedAt = DateTimeOffset.UtcNow
  ├── SerializeRecord(record) → json
  ├── db.StringSetAsync(key, json, TTL)
  └── return true
```

## 性能指标

`ConversationStoreMetrics` 收集以下指标：

| 指标方法 | 说明 |
|----------|------|
| `RecordHit()` | 缓存命中 |
| `RecordMiss()` | 缓存未命中 |
| `RecordReadLatency(ms)` | 读取延迟 |
| `RecordWriteLatency(ms)` | 写入延迟 |
| `RecordReadFailure()` | 读取失败 |
| `RecordWriteFailure()` | 写入失败 |
| `RecordMessagesLoaded(count)` | 加载消息数 |
| `RecordMessagesWritten(count)` | 写入消息数 |
| `RecordColdArchiveSuccess()` | 冷归档成功 |
| `RecordColdArchiveFailure()` | 冷归档失败 |
| `RecordColdArchiveLatency(ms)` | 冷归档延迟 |

所有操作使用 `Stopwatch` 计时，在 `finally` 块中记录延迟。

## 错误处理

- `GetDatabase()` 失败时返回 null，上层方法返回空/false
- 读取异常：记录 Error 日志，返回空集合/null
- 写入异常：记录 Error 日志，返回 false/Conflict
- 所有异常不向上传播
