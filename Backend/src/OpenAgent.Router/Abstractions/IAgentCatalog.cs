using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IAgentCatalog
{
    Task<IReadOnlyList<RoutableAgent>> ListAsync(
        AgentCatalogRequest request,
        CancellationToken cancellationToken);
}
