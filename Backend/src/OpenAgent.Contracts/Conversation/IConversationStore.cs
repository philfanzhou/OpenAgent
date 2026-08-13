namespace OpenAgent.Contracts.Conversation;

public interface IConversationStore
{
    /// <summary>
    /// 获取会话的最近 N 条消息，按 Sequence 升序返回。
    /// </summary>
    Task<IReadOnlyList<ConversationMessage>> GetMessagesAsync(
        string tenantId,
        string conversationId,
        int maxMessages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 分页获取会话消息，按 Sequence 升序返回。
    /// </summary>
    Task<IReadOnlyList<ConversationMessage>> GetMessagesPagedAsync(
        string tenantId,
        string conversationId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取完整会话记录（含元数据和全部消息）。
    /// </summary>
    Task<ConversationRecord?> GetRecordAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default);

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
    /// 更新会话标题（用于 LLM 摘要回写）。
    /// </summary>
    Task<bool> UpdateTitleAsync(
        string tenantId,
        string conversationId,
        string title,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 列出指定租户的会话，按 LastMessageAt 降序返回（不含消息体）。
    /// </summary>
    Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按关键词搜索会话（在消息内容中匹配），按 LastMessageAt 降序返回（不含消息体）。
    /// </summary>
    Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId,
        string keyword,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 软删除会话：设置 IsDeletedByUser=true，数据保留供审计。用户侧查询自动过滤。
    /// </summary>
    Task<bool> SoftDeleteAsync(
        string tenantId,
        string conversationId,
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
