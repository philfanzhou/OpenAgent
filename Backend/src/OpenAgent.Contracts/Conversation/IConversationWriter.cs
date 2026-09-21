namespace OpenAgent.Contracts.Conversation;

/// <summary>会话存储写侧契约；PostgreSQL 为唯一事实源，Redis 热副本仅由写穿透装饰器回填。</summary>
public interface IConversationWriter
{
    /// <summary>
    /// 创建新会话记录。如果已存在则返回 false。
    /// </summary>
    Task<bool> CreateAsync(
        ConversationRecord record,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 追加消息到已有会话。使用乐观锁：如果 record.Version 与存储版本不匹配则失败。
    /// 成功后 Version 自增 1。
    /// </summary>
    Task<AppendResult> AppendMessagesAsync(
        string tenantId,
        string conversationId,
        int expectedVersion,
        IReadOnlyList<ConversationMessage> messages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新会话状态（Running/Completed/Failed/Cancelled）。
    /// </summary>
    Task<bool> UpdateStatusAsync(
        string tenantId,
        string conversationId,
        ConversationStatus status,
        int expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 软删除会话：设置 IsDeletedByUser=true，数据保留供审计。用户侧查询自动过滤。
    /// </summary>
    Task<bool> SoftDeleteAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a context compression audit record without modifying stored messages.
    /// </summary>
    Task<bool> RecordCompressionAsync(
        string tenantId,
        string conversationId,
        ContextSummary summary,
        CancellationToken cancellationToken = default);
}

public sealed class AppendResult
{
    public bool Success { get; init; }
    public int NewVersion { get; init; }
    public int NewMessageCount { get; init; }
    public int SkippedDuplicateCount { get; init; }
    public string? ConflictReason { get; init; }

    public static AppendResult Ok(int newVersion, int newMessageCount) =>
        new() { Success = true, NewVersion = newVersion, NewMessageCount = newMessageCount };

    public static AppendResult Ok(int newVersion, int newMessageCount, int skippedDuplicateCount) =>
        new() { Success = true, NewVersion = newVersion, NewMessageCount = newMessageCount, SkippedDuplicateCount = skippedDuplicateCount };

    public static AppendResult Conflict(string reason) =>
        new() { Success = false, ConflictReason = reason };
}
