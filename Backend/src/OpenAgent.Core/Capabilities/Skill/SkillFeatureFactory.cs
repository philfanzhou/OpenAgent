using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Tooling.Abstractions;

namespace OpenAgent.Core.Capabilities.Skill;

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
            ContextProviders = runtime.Provider is { } provider ? [provider] : [],
            Resource = runtime
        };
    }
}
