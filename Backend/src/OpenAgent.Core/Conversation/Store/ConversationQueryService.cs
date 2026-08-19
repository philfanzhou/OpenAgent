using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Conversation.Store;

internal sealed class ConversationQueryService : IConversationQueryService
{
    private readonly IConversationStore _store;
    private readonly ICurrentUserContext _currentUser;

    public ConversationQueryService(
        IConversationStore store,
        ICurrentUserContext currentUser)
    {
        _store = store;
        _currentUser = currentUser;
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
        return SoftDeleteVisibleAsync(tenantId, conversationId, cancellationToken);
    }

    public async Task<ConversationRecord?> GetRecordAsync(
        string tenantId, string conversationId, CancellationToken cancellationToken = default)
    {
        ConversationRecord? record = await _store.GetRecordAsync(
            tenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        return IsVisibleToCurrentUser(record) ? record : null;
    }

    private async Task<bool> SoftDeleteVisibleAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        ConversationRecord? record = await GetRecordAsync(
            tenantId,
            conversationId,
            cancellationToken).ConfigureAwait(false);
        return record != null
            && await _store.SoftDeleteAsync(
                tenantId,
                conversationId,
                cancellationToken).ConfigureAwait(false);
    }

    private bool IsVisibleToCurrentUser(ConversationRecord? record) =>
        record != null
        && !record.IsDeletedByUser
        && record.Type == ConversationType.User
        && record.OwnerRole == ConversationOwnerRole.User
        && string.Equals(record.UserId, _currentUser.UserId, StringComparison.OrdinalIgnoreCase);
}
