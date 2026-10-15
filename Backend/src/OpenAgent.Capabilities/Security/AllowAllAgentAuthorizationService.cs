using OpenAgent.Contracts.Security;

namespace OpenAgent.Capabilities.Security;

internal sealed class AllowAllAgentAuthorizationService : IAgentAuthorizationService
{
    public Task<bool> IsAuthorizedAsync(
        AgentAuthorizationRequest request,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
    }
}
