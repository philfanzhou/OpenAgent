using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Security;

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
