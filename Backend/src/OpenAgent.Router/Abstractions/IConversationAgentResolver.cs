using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IConversationAgentResolver
{
    Task<ConversationAgentResolution> ResolveAsync(
        string targetEndpoint,
        string conversationId,
        EngineRequestIdentity identity,
        CancellationToken cancellationToken);
}
