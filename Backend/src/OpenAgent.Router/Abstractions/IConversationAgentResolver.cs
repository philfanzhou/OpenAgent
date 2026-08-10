using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IConversationAgentResolver
{
    Task<ConversationAgentResolution> ResolveAsync(
        string targetEndpoint,
        string conversationId,
        HttpContext context,
        CancellationToken cancellationToken);
}
