using OpenAgent.Contracts.Conversation;

namespace OpenAgent.Core.Conversation.Store;

internal sealed class ConversationQueryService : IConversationQueryService
{
    private readonly IConversationStore _store;

    public ConversationQueryService(IConversationStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
        string tenantId, int skip, int take, CancellationToken cancellationToken = default)
    {
        return await _store.ListConversationsAsync(tenantId, skip, take, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ConversationRecord>> SearchConversationsAsync(
        string tenantId, string keyword, int skip, int take, CancellationToken cancellationToken = default)
    {
        return await _store.SearchConversationsAsync(tenantId, keyword, skip, take, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> SoftDeleteAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        return _store.SoftDeleteAsync(tenantId, conversationId, cancellationToken);
    }

    public async Task<ConversationRecord?> GetRecordAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        return await _store.GetRecordAsync(
            tenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
    }
}
