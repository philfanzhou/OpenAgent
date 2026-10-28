using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IConversationProviderStore
{
    Task<ConversationProviderAffinity?> GetAsync(
        string tenantId,
        string conversationId,
        CancellationToken cancellationToken = default);

    Task SetAsync(
        string tenantId,
        string conversationId,
        ConversationProviderAffinity affinity,
        CancellationToken cancellationToken = default);

    Task<ConversationProviderAffinity> BindAsync(
        string tenantId,
        string conversationId,
        ConversationProviderAffinity affinity,
        CancellationToken cancellationToken = default);
}
