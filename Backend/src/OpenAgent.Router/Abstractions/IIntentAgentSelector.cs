using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
namespace OpenAgent.Router;

internal interface IIntentAgentSelector
{
    Task<string?> SelectAsync(
        string message,
        IReadOnlyList<AgentSummary> candidates,
        IAgentUserContext userContext,
        CancellationToken cancellationToken);
}
