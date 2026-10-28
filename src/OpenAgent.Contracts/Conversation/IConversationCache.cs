namespace OpenAgent.Contracts.Conversation;

/// <summary>
/// Optional hot replica for conversation records. Implementations must never be
/// treated as the durable source of truth.
/// </summary>
public interface IConversationCache
{
    Task<ConversationRecord?> GetAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default);

    Task SetAsync(ConversationRecord record, CancellationToken cancellationToken = default);

    Task RemoveAsync(string tenantId, string conversationId, CancellationToken cancellationToken = default);
}
