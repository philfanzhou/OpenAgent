namespace OpenAgent.Contracts.Conversation;

/// <summary>会话存储读侧契约；纯读消费方依赖本接口而非完整存储。</summary>
public interface IConversationReader
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
    /// 列出指定租户的会话，按 LastMessageAt 降序返回（不含消息体）。
    /// 仅返回当前认证用户（ICurrentUserContext）拥有的会话。
    /// </summary>
    Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按关键词搜索会话（在消息内容中匹配），按 LastMessageAt 降序返回（不含消息体）。
    /// 仅搜索当前认证用户（ICurrentUserContext）拥有的会话。
    /// </summary>
    Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId,
        string keyword,
        int skip,
        int take,
        CancellationToken cancellationToken = default);
}
