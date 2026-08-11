namespace OpenAgent.Authorization;

public sealed record AuthorizationSubject(
    string SubjectId,
    string? TenantId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Groups,
    IReadOnlyDictionary<string, string> Claims);

public sealed record AuthorizationRequest(
    AuthorizationSubject Subject,
    string Permission,
    string? ResourceId = null);

public sealed record AuthorizationDecision(
    bool IsAllowed,
    IReadOnlySet<string> GrantedPermissions);

public interface IPermissionAuthorizationService
{
    IReadOnlySet<string> GetPermissions(AuthorizationSubject subject);

    AuthorizationDecision Authorize(AuthorizationRequest request);
}

public sealed record DelegatedAuthorization(
    AuthorizationSubject Subject,
    IReadOnlySet<string> Permissions,
    string? Audience = null)
{
    public static DelegatedAuthorization Create(
        AuthorizationSubject subject,
        IEnumerable<string> grantedPermissions,
        string? audience = null) => new(
            subject,
            Normalize(grantedPermissions),
            audience);

    public static DelegatedAuthorization Restrict(
        AuthorizationSubject subject,
        IEnumerable<string> grantedPermissions,
        IEnumerable<string> requestedPermissions,
        string? audience = null)
    {
        IReadOnlySet<string> granted = Normalize(grantedPermissions);
        IReadOnlySet<string> requested = Normalize(requestedPermissions);
        foreach (string permission in requested)
        {
            (string requiredPermission, string? resourceId) = Parse(permission);
            if (!PermissionMatcher.IsAllowed(granted, requiredPermission, resourceId))
            {
                throw new UnauthorizedAccessException(
                    $"Cannot delegate permission '{permission}' that the subject does not hold.");
            }
        }

        return new DelegatedAuthorization(subject, requested, audience);
    }

    private static IReadOnlySet<string> Normalize(IEnumerable<string> permissions) =>
        permissions
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Select(permission => permission.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static (string Permission, string? ResourceId) Parse(string permission)
    {
        int separator = permission.IndexOf(':', StringComparison.Ordinal);
        return separator < 0
            ? (permission, null)
            : (permission[..separator], permission[(separator + 1)..]);
    }
}

public interface IDelegatedAuthorizationIssuer
{
    string Issue(DelegatedAuthorization authorization);
}
