using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Application.Tooling.Abstractions;

/// <summary>Contributes tools, context providers and resources for one execution.</summary>
internal interface IAgentFeatureFactory
{
    Task<AgentFeature> CreateAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken);
}
