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
