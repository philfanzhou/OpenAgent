namespace OpenAgent.Contracts.Conversation;

/// <summary>
/// Query-side API for conversation metadata. Separated from IConversationStore
/// (command-side) to allow independent scaling and permission control.
/// Only exposes conversation headers (title/id), not message details.
/// Message details are retrieved internally via IConversationStore when
/// building agent context.
/// </summary>
public interface IConversationQueryService
{
    /// <summary>
    /// List conversation records for a tenant, ordered by LastMessageAt descending.
    /// Returns metadata only (no message bodies).
    /// </summary>
    Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Search conversations by keyword in message content.
    /// Returns metadata only (no message bodies).
    /// </summary>
    Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId,
        string keyword,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a single conversation record by ID. Returns null if not found.
    /// </summary>
    Task<ConversationRecord?> GetRecordAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 软删除会话：设置 IsDeletedByUser=true，数据保留供审计。
    /// </summary>
    Task<bool> SoftDeleteAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default);
}
