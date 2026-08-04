# Redis — 规格说明

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
