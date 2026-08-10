using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IAgentSelectionService
{
    Task<string?> SelectAsync(
        AgentSelectionRequest request,
        CancellationToken cancellationToken);
}
