using OpenAgent.Contracts.Configuration;
using OpenAgent.Router.Models;
namespace OpenAgent.Router;

internal interface IIntentAgentSelector
{
    Task<string?> SelectAsync(
        AgentProviderRequestContext requestContext,
        string message,
        IReadOnlyList<AgentSummary> candidates,
        CancellationToken cancellationToken);
}
