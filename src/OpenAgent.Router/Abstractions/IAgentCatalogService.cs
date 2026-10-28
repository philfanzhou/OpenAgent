using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IAgentCatalogService
{
    Task<IReadOnlyList<AgentCatalogEntry>> GetAuthorizedAsync(
        AgentProviderRequestContext requestContext,
        CancellationToken cancellationToken = default);

    Task<AgentCatalogEntry> ResolveAsync(
        AgentProviderRequestContext requestContext,
        string agentId,
        CancellationToken cancellationToken = default);
}
