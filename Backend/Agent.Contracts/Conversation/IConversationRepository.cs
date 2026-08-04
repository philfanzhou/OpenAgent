namespace OpenAgent.Contracts.Conversation;

/// <summary>
/// Abstracts CRUD operations for conversation cold storage across different database providers.
/// </summary>
public interface IConversationRepository : IDisposable
{
    Task EnsureInitializedAsync(CancellationToken cancellationToken = default);

    Task ArchiveAsync(ConversationRecord record, CancellationToken cancellationToken = default);

    Task ArchiveMessagesAsync(string tenantId, string conversationId,
        IReadOnlyList<ConversationMessage> messages, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationMessage>> LoadMessagesAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default);

    Task<ConversationRecord?> GetRecordAsync(string tenantId, string conversationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List conversation records for a tenant, ordered by LastMessageAt descending.
    /// </summary>
    Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId, int skip, int take, CancellationToken cancellationToken = default);

    /// <summary>
    /// Search conversations by keyword in message content.
    /// </summary>
    Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default);
}
