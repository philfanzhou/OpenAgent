using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IConversationProviderResolver
{
    Task<ConversationProviderAffinity?> ResolveAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        CancellationToken cancellationToken = default);

    Task<ConversationProviderAffinity> BindPendingAsync(
        AgentProviderRequestContext requestContext,
        string conversationId,
        string providerId,
        CancellationToken cancellationToken = default);
}
