using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Router;

public interface IAgentAccessControl
{
    Task<IReadOnlyList<AgentSummary>> GetAuthorizedAgentsAsync(
        IAgentUserContext userContext,
        IReadOnlyList<AgentSummary> agents,
        CancellationToken cancellationToken = default);
}
