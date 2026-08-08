using OpenAgent.Contracts.Configuration;
using OpenAgent.Router.Options;

namespace OpenAgent.Router;

internal interface IExternalAgentRegistry
{
    IReadOnlyList<AgentSummary> ListAgents();

    bool TryGet(string agentId, out ExternalAgentOptions? agent);
}
