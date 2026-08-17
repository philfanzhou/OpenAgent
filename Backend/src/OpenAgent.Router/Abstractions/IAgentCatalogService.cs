using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IAgentCatalogService
{
    Task<AgentCatalogSnapshot> GetAuthorizedAsync(
        AgentProviderRequestContext requestContext,
        CancellationToken cancellationToken = default);

    Task<AgentCatalogEntry> ResolveAsync(
        AgentProviderRequestContext requestContext,
        string agentId,
        CancellationToken cancellationToken = default);
}
