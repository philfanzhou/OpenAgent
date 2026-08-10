using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IAgentSelectionService
{
    Task<AgentSelectionResult> SelectAsync(
        AgentSelectionRequest request,
        CancellationToken cancellationToken);
}
