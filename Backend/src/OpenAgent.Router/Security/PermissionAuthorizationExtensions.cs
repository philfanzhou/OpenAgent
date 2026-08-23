using OpenAgent.Contracts.Security;
using OpenAgent.Authorization;

namespace OpenAgent.Router;

internal static class PermissionAuthorizationExtensions
{
    internal static IReadOnlySet<string> GetPermissions(
        this IPermissionAuthorizationService authorization,
        IAgentUserContext userContext)
    {
        return TryCreateSubject(userContext, out AuthorizationSubject? subject)
            ? authorization.GetPermissions(subject!)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static bool IsAuthorized(
        this IPermissionAuthorizationService authorization,
        IAgentUserContext userContext,
        string permission,
        string? resourceId = null)
    {
        return TryCreateSubject(userContext, out AuthorizationSubject? subject)
            && authorization.Authorize(new AuthorizationRequest(subject!, permission, resourceId)).IsAllowed;
    }

    internal static DelegatedAuthorization CreateDelegation(
        this IPermissionAuthorizationService authorization,
        IAgentUserContext userContext,
        string? audience = null)
    {
        AuthorizationSubject subject = GetSubject(userContext);
        return DelegatedAuthorization.Create(subject, authorization.GetPermissions(subject), audience);
    }

    internal static DelegatedAuthorization CreateRestrictedDelegation(
        this IPermissionAuthorizationService authorization,
        IAgentUserContext userContext,
        IEnumerable<string> permissions,
        string? audience = null)
    {
        AuthorizationSubject subject = GetSubject(userContext);
        return DelegatedAuthorization.Restrict(
            subject,
            authorization.GetPermissions(subject),
            permissions,
            audience);
    }

    internal static DelegatedAuthorization CreateAgentDelegation(
        this IPermissionAuthorizationService authorization,
        IAgentUserContext userContext,
        string? agentId,
        string? audience = null)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return authorization.CreateDelegation(userContext, audience);
        }

        AuthorizationSubject subject = GetSubject(userContext);
        IReadOnlySet<string> granted = authorization.GetPermissions(subject);
        HashSet<string> requested = granted
            .Where(permission => !permission.Equals("*", StringComparison.OrdinalIgnoreCase)
                && !permission.Equals(PermissionCatalog.AgentExecute, StringComparison.OrdinalIgnoreCase)
                && !permission.StartsWith(
                    $"{PermissionCatalog.AgentExecute}:",
                    StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (granted.Contains("*"))
        {
            requested.UnionWith(PermissionCatalog.All.Where(permission =>
                !permission.Equals(PermissionCatalog.AgentExecute, StringComparison.OrdinalIgnoreCase)));
        }
        requested.Add($"{PermissionCatalog.AgentExecute}:{agentId}");

        return DelegatedAuthorization.Restrict(
            subject,
            granted,
            requested,
            audience);
    }

    private static AuthorizationSubject GetSubject(IAgentUserContext userContext) =>
        TryCreateSubject(userContext, out AuthorizationSubject? subject)
            ? subject!
            : throw new UnauthorizedAccessException("Authentication is required before authorization delegation.");

    private static bool TryCreateSubject(
        IAgentUserContext userContext,
        out AuthorizationSubject? subject)
    {
        subject = null;
        if (!userContext.IsAuthenticated)
        {
            return false;
        }

        subject = new AuthorizationSubject(
        userContext.UserId,
        userContext.TenantId,
        userContext.Roles,
        userContext.Groups,
        userContext.Claims);
        return true;
    }
}
