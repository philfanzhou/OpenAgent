using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IIntentAgentSelector
{
    Task<string?> SelectAsync(
        AgentSelectionRequest request,
        CancellationToken cancellationToken);
}
