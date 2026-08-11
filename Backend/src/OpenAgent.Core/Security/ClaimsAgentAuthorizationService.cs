using OpenAgent.Authorization;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Security;

internal sealed class ClaimsAgentAuthorizationService : IAgentAuthorizationService
{
    public Task<bool> IsAuthorizedAsync(
        AgentAuthorizationRequest request,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default)
    {
        if (!userContext.IsAuthenticated) return Task.FromResult(false);
        string resource = request.ResourceType.ToString().ToLowerInvariant();
        string requiredPermission = $"{resource}.{request.Action.ToLowerInvariant()}";
        bool allowed = PermissionMatcher.IsAllowed(
            PermissionMatcher.ReadPermissions(userContext.Claims),
            requiredPermission,
            request.ResourceId);
        return Task.FromResult(allowed);
    }
}
