namespace OpenAgent.Contracts.Conversation;

public sealed class ConversationRecord
{
    public required string ConversationId { get; init; }
    public required string TenantId { get; init; }
    public required string UserId { get; init; }
    public ConversationType Type { get; init; } = ConversationType.User;
    public string? AgentId { get; set; }
    public string? TraceId { get; set; }
    public int Version { get; set; } = 1;
    public ConversationStatus Status { get; set; } = ConversationStatus.Running;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastMessageAt { get; set; } = DateTimeOffset.UtcNow;
    public int MessageCount { get; set; }
    public List<ConversationMessage> Messages { get; set; } = [];

    /// <summary>
    /// 会话标题，用于用户侧列表展示和搜索。首轮取用户消息截取，后续异步 LLM 摘要更新。
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// 用户软删除标记。true 表示用户已删除该会话（用户不可见、不可搜索），数据保留供审计。
    /// </summary>
    public bool IsDeletedByUser { get; set; }

    /// <summary>
    /// 用户软删除时间，UTC。null 表示未删除。
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// 归档入库时间，UTC。用于数据分层迁移判断（超过保留周期则迁移到归档表）。
    /// </summary>
    public DateTimeOffset ArchivedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ConversationType
{
    User = 0,
    Internal = 1,
    Channel = 2
}

public enum ConversationStatus
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}
