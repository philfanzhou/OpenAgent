
## Feature


## 核心能力

Agent.Core 使用 Redis 作为会话热存储，提供高频读写能力。通过 `IConversationStore` 接口抽象，`RedisConversationStore` 为主要实现。

## 关键接口与类

| 接口/类 | 所在文件 | 职责 |
|---------|----------|------|
| `IConversationStore` | `Agent.Contracts/Conversation/IConversationStore.cs` | 会话存储统一接口 |
| `RedisConversationStore` | `src/Core/Conversation/Store/RedisConversationStore.cs` | Redis 会话存储实现 |
| `ConversationRecord` | `Agent.Contracts/Conversation/` | 会话记录模型 |
| `ConversationMessage` | `Agent.Contracts/Conversation/` | 会话消息模型 |
| `AppendResult` | `Agent.Contracts/Conversation/IConversationStore.cs` | 追加操作结果 |
| `ConversationStoreOptions` | `Agent.Contracts/Conversation/ConversationStoreOptions.cs` | 存储配置选项 |
| `ConversationStoreMetrics` | — | 存储指标收集 |

## 功能范围

- 会话记录的创建（`CreateAsync`，NX 语义）
- 会话消息的追加（`AppendMessagesAsync`，乐观并发）
- 会话记录的读取（`GetRecordAsync`）
- 会话消息的读取（`GetMessagesAsync`，支持最近 N 条）
- 会话状态更新（`UpdateStatusAsync`，乐观并发）
- TTL 自动过期
- 性能指标收集（命中率、延迟、失败计数）

## IConversationStore 核心方法

```csharp
Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(string tenantId, string conversationId, int maxMessages, CancellationToken ct = default);
Task<ConversationRecord?> GetRecordAsync(string tenantId, string conversationId, CancellationToken ct = default);
Task<bool> CreateAsync(ConversationRecord record, CancellationToken ct = default);
Task<AppendResult> AppendMessagesAsync(string tenantId, string conversationId, int expectedVersion, IReadOnlyList<ConversationMessage> messages, CancellationToken ct = default);
Task<bool> UpdateStatusAsync(string tenantId, string conversationId, ConversationStatus status, int expectedVersion, CancellationToken ct = default);
```

## Specification


## 接口契约

### IConversationStore

```csharp
// Agent.Contracts/Conversation/IConversationStore.cs
public interface IConversationStore
{
    Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string tenantId, string conversationId, int maxMessages, CancellationToken ct = default);
    Task<ConversationRecord?> GetRecordAsync(
        string tenantId, string conversationId, CancellationToken ct = default);
    Task<bool> CreateAsync(ConversationRecord record, CancellationToken ct = default);
    Task<AppendResult> AppendMessagesAsync(
        string tenantId, string conversationId, int expectedVersion,
        IReadOnlyList<ConversationMessage> messages, CancellationToken ct = default);
    Task<bool> UpdateStatusAsync(
        string tenantId, string conversationId, ConversationStatus status,
        int expectedVersion, CancellationToken ct = default);
}
```

### AppendResult

```csharp
public sealed class AppendResult
{
    public bool Success { get; init; }
    public int NewVersion { get; init; }
    public int NewMessageCount { get; init; }
    public string? ConflictReason { get; init; }

    public static AppendResult Ok(int newVersion, int newMessageCount);
    public static AppendResult Conflict(string reason);
}
```

### ConversationStoreOptions

```csharp
// Agent.Contracts/Conversation/ConversationStoreOptions.cs
public sealed class ConversationStoreOptions
{
    public const string SectionName = "ConversationStore";
    public int MaxHistoryMessages { get; set; } = 20;          // 历史消息窗口大小
    public int RedisTtlMinutes { get; set; } = 30;             // Redis TTL（分钟）
    public bool EnableColdArchive { get; set; } = true;        // 是否启用冷归档
    public string? ColdArchiveConnectionString { get; set; }   // 冷归档连接字符串
    public int ColdArchiveRetryCount { get; set; } = 3;        // 冷归档重试次数
    public int ColdArchiveRetryDelayMs { get; set; } = 1000;   // 冷归档重试延迟（ms）
}
```

## Redis 数据结构

### 存储方式

- 使用 **String** 类型存储整个 `ConversationRecord`（JSON 序列化）
- Key 格式：`conversation:{tenantId}:{conversationId}`
- 每次写入刷新 TTL

### 序列化

- 使用 `System.Text.Json`
- `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`
- `WriteIndented = false`

### TTL

- 由 `ConversationStoreOptions.RedisTtlMinutes` 配置（默认 **30 分钟**）
- 每次写入（Create/Append/UpdateStatus）刷新 TTL

## 乐观并发控制

- `ConversationRecord.Version` 字段用于乐观并发
- `AppendMessagesAsync` 和 `UpdateStatusAsync` 需要 `expectedVersion` 参数
- 版本不匹配时返回 `AppendResult.Conflict`
- 成功操作后 `Version` 自增 1

## 创建语义

- `CreateAsync` 使用 `When.NotExists`（NX 语义）
- Key 已存在时返回 `false`
- 首次创建时设置 TTL

## Design


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

## Tasks


## 已完成

- [x] `IConversationStore` 接口定义（5 个方法）
- [x] `AppendResult` 结果模型（Ok/Conflict 静态工厂方法）
- [x] `ConversationStoreOptions` 配置选项
- [x] `RedisConversationStore` 完整实现
- [x] Key 格式 `conversation:{tenantId}:{conversationId}`
- [x] JSON 序列化（CamelCase 命名策略）
- [x] 乐观并发控制（Version 字段）
- [x] Create 使用 `When.NotExists`（NX 语义）
- [x] TTL 由 `RedisTtlMinutes` 配置（默认 30 分钟）
- [x] 性能指标收集（`ConversationStoreMetrics`）
- [x] `Stopwatch` 计时记录延迟
- [x] 错误处理（不向上传播异常）

## 待办

- [ ] Redis 集群模式下的兼容性说明
- [ ] 大消息量场景的性能优化（当前整体序列化）
- [ ] 消息分页读取支持

## Tests


## 单元测试

### RedisConversationStore CRUD

- `CreateAsync` 正确创建会话记录，设置 TTL
- `CreateAsync` Key 已存在时返回 false
- `GetRecordAsync` 正确读取会话记录
- `GetRecordAsync` Key 不存在时返回 null
- `GetMessagesAsync` 返回最近 N 条消息
- `GetMessagesAsync` 消息数不足 N 条时返回全部
- `GetMessagesAsync` Key 不存在时返回空集合

### 乐观并发

- `AppendMessagesAsync` 版本匹配时成功追加
- `AppendMessagesAsync` 版本不匹配时返回 Conflict
- `AppendMessagesAsync` 记录不存在时返回 Conflict
- `AppendMessagesAsync` 成功后 Version 自增 1
- `AppendMessagesAsync` 成功后 MessageCount 更新
- `AppendMessagesAsync` 成功后 UpdatedAt 更新
- `AppendMessagesAsync` 成功后 LastMessageAt 更新
- `UpdateStatusAsync` 版本匹配时成功更新
- `UpdateStatusAsync` 版本不匹配时返回 false
- `UpdateStatusAsync` 记录不存在时返回 false

### 序列化

- JSON 使用 CamelCase 命名策略
- 序列化/反序列化往返一致性

### Key 格式

- Key 格式为 `conversation:{tenantId}:{conversationId}`

### 错误处理

- Redis 连接失败时返回安全的默认值
- 读取异常时返回空集合/null
- 写入异常时返回 false/Conflict

## 集成测试

- Redis 连接与断线场景
- DualWrite 场景下的 Redis 写入
- TTL 过期后数据不可读
- 并发写入的乐观并发冲突

## Conventions


## Key 命名

- 会话记录：`conversation:{tenantId}:{conversationId}`
- Key 包含租户 ID，实现多租户隔离
- Key 前缀固定为 `conversation:`

## TTL 策略

- 会话记录 TTL 由 `ConversationStoreOptions.RedisTtlMinutes` 配置
- 默认 **30 分钟**
- 每次写入（Create/Append/UpdateStatus）刷新 TTL
- TTL 到期后数据从 SQL Server 冷归档恢复

## 序列化约定

- 使用 `System.Text.Json`
- `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`
- `WriteIndented = false`（紧凑格式）
- 整个 `ConversationRecord` 序列化为单个 String 值

## 乐观并发

- `AppendMessagesAsync` 和 `UpdateStatusAsync` 需要 `expectedVersion` 参数
- 版本不匹配时操作失败，返回 Conflict/false
- 成功操作后 `Version` 自增 1
- 调用方负责处理版本冲突（重新加载版本号后重试）

## 创建语义

- `CreateAsync` 使用 `When.NotExists`（NX 语义）
- Key 已存在时不覆盖，返回 false
- 防止并发创建同一会话

## 降级到 InMemory

- Redis 不可用时自动降级到 `InMemoryConversationStore`
- 降级触发条件：`GetDatabase()` 返回 null
- 降级后数据仅存在于内存，进程重启后丢失
- Redis 恢复后自动切回，不自动同步内存数据到 Redis

## 连接管理

- 使用 `IConnectionMultiplexer`（StackExchange.Redis）管理连接
- 连接由 DI 容器注入，`RedisConversationStore` 不负责创建
- 支持 Sentinel 和 Cluster 模式（由连接字符串配置）

## 错误处理

- 所有异常不向上传播
- 读取异常返回安全的默认值（空集合/null）
- 写入异常返回 false/Conflict
- 异常记录 Error 日志

## 性能指标

- 所有操作使用 `Stopwatch` 计时
- 在 `finally` 块中记录延迟
- 指标通过 `ConversationStoreMetrics` 收集
- 指标包括：命中率、延迟、失败计数、消息数
