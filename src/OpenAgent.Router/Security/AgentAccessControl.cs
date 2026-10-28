using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Router.Security;

internal sealed class AgentAccessControl(
    IAgentVisibilityService visibilityService) : IAgentAccessControl
{
    public async Task<IReadOnlyList<AgentSummary>> GetAuthorizedAgentsAsync(
        IAgentUserContext userContext,
        IReadOnlyList<AgentSummary> agents,
        CancellationToken cancellationToken)
    {
        List<AgentSummary> authorized = [];
        foreach (AgentSummary agent in agents)
        {
            if (await visibilityService.IsAgentVisibleToUserAsync(
                agent.AgentId,
                userContext,
                cancellationToken).ConfigureAwait(false))
            {
                authorized.Add(agent);
            }
        }

        return authorized;
    }
}
