using OpenAgent.Contracts.Configuration;
namespace OpenAgent.Router;

internal interface IIntentAgentSelector
{
    Task<string?> SelectAsync(
        string message,
        IReadOnlyList<AgentSummary> candidates,
        CancellationToken cancellationToken);
}
