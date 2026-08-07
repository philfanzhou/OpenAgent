using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IIntentAgentSelector
{
    Task<IntentAgentDecision?> SelectAsync(
        IntentAgentSelectionRequest request,
        CancellationToken cancellationToken);
}
