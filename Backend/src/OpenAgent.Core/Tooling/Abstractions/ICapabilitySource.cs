using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Tooling.Abstractions;

internal interface ICapabilitySource
{
    Task<IReadOnlyList<CapabilityDefinition>> DiscoverAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken);
}
