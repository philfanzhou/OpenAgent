using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authorization;

namespace OpenAgent.Router;

internal static class GatewayAuthorizationExtensions
{
    internal static IReadOnlySet<string> ResolvePermissions(
        this IGatewayAuthorizationService authorization,
        IAgentUserContext userContext) => authorization.ResolvePermissions(ToIdentity(userContext));

    internal static bool IsAuthorized(
        this IGatewayAuthorizationService authorization,
        IAgentUserContext userContext,
        string requiredPermission,
        string? resourceId = null) => authorization.IsAuthorized(
            ToIdentity(userContext),
            requiredPermission,
            resourceId);

    internal static string IssueGrant(
        this IGatewayAuthorizationService authorization,
        IAgentUserContext userContext,
        string? audience = null) => authorization.IssueGrant(
            ToIdentity(userContext),
            audience);

    internal static string IssueRestrictedGrant(
        this IGatewayAuthorizationService authorization,
        IAgentUserContext userContext,
        IEnumerable<string> permissions,
        string? audience = null) => authorization.IssueRestrictedGrant(
            ToIdentity(userContext),
            permissions,
            audience);

    private static GatewayIdentity ToIdentity(IAgentUserContext userContext) => new(
        userContext.UserId,
        userContext.TenantId,
        userContext.Roles,
        userContext.Groups,
        userContext.Claims,
        userContext.IsAuthenticated);
}
