using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;
using OpenAgent.Router.Options;

namespace OpenAgent.Router.Routing;

internal sealed class ExternalAgentRegistry : IExternalAgentRegistry
{
    private readonly IReadOnlyDictionary<string, ExternalAgentOptions> _agents;
    private readonly IReadOnlyList<AgentSummary> _summaries;

    public ExternalAgentRegistry(IOptions<ExternalAgentRoutingOptions> options)
    {
        _agents = options.Value.Agents.ToDictionary(
            agent => agent.AgentId,
            StringComparer.OrdinalIgnoreCase);
        _summaries = options.Value.Agents
            .OrderBy(agent => agent.AgentId, StringComparer.OrdinalIgnoreCase)
            .Select(agent => new AgentSummary
            {
                AgentId = agent.AgentId,
                Name = string.IsNullOrWhiteSpace(agent.Name) ? agent.AgentId : agent.Name,
                Description = agent.Description,
                Status = (int)AgentPublishStatus.Snapshot,
                CurrentVersion = "external",
                ApiFormat = agent.Adapter
            })
            .ToArray();
    }

    public IReadOnlyList<AgentSummary> ListAgents() => _summaries;

    public bool TryGet(string agentId, out ExternalAgentOptions? agent) =>
        _agents.TryGetValue(agentId, out agent);
}
