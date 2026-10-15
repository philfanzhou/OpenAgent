using OpenAgent.Contracts.Security;

namespace OpenAgent.Capabilities.Security;

public interface IAgentAuthorizationService
{
    Task<bool> IsAuthorizedAsync(
        AgentAuthorizationRequest request,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default);
}
