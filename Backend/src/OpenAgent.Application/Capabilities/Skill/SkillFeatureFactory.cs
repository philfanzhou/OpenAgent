using OpenAgent.Contracts.Capabilities;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Application.Tooling.Abstractions;

namespace OpenAgent.Application.Capabilities.Skill;

internal sealed class SkillFeatureFactory(AgentSkillsProviderFactory skills) : IAgentFeatureFactory
{
    public async Task<AgentFeature> CreateAsync(
        string agentId,
        AgentConfig config,
        IAgentUserContext user,
        CancellationToken cancellationToken)
    {
        AgentSkillsRuntime runtime = await skills.CreateAsync(
            agentId, config, user, cancellationToken).ConfigureAwait(false);
        return new AgentFeature
        {
            Skills = runtime.Skills,
            Tools = runtime.Tools,
            Resource = runtime
        };
    }
}
