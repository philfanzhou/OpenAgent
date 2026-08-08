using OpenAgent.Contracts.Configuration;
using OpenAgent.Router.Models;

namespace OpenAgent.Router;

internal interface IEngineAgentClient
{
    Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(
        string engineEndpoint,
        DownstreamRequestIdentity identity,
        CancellationToken cancellationToken);

    Task<string?> ChatAsync(
        string engineEndpoint,
        DownstreamRequestIdentity identity,
        string agentId,
        string message,
        CancellationToken cancellationToken);
}
