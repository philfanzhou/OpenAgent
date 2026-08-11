using OpenAgent.Contracts.Security;
using OpenAgent.Authorization;

namespace OpenAgent.Router;

internal static class PermissionAuthorizationExtensions
{
    internal static IReadOnlySet<string> ResolvePermissions(
        this IPermissionAuthorizer authorizer,
        IAgentUserContext userContext) => authorizer.ResolvePermissions(
            ToSubject(userContext));

    internal static bool IsAuthorized(
        this IPermissionAuthorizer authorizer,
        IAgentUserContext userContext,
        string permission,
        string? resourceId = null) => authorizer.IsAuthorized(
            ToSubject(userContext),
            permission,
            resourceId);

    internal static string Issue(
        this IDelegatedPermissionGrantIssuer issuer,
        IAgentUserContext userContext,
        string? audience = null) => issuer.Issue(
            ToSubject(userContext),
            audience);

    internal static string IssueRestricted(
        this IDelegatedPermissionGrantIssuer issuer,
        IAgentUserContext userContext,
        IEnumerable<string> permissions,
        string? audience = null) => issuer.IssueRestricted(
            ToSubject(userContext),
            permissions,
            audience);

    private static PermissionSubject ToSubject(IAgentUserContext userContext) => new(
        userContext.UserId,
        userContext.TenantId,
        userContext.Roles,
        userContext.Groups,
        userContext.Claims,
        userContext.IsAuthenticated);
}
