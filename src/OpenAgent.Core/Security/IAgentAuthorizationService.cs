using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Security;

public interface IAgentAuthorizationService
{
    Task<bool> IsAuthorizedAsync(
        AgentAuthorizationRequest request,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default);
}
