namespace OpenAgent.Authorization;

public sealed record PermissionSubject(
    string SubjectId,
    string? TenantId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Groups,
    IReadOnlyDictionary<string, string> Claims,
    bool IsAuthenticated);

public interface IPermissionAuthorizer
{
    IReadOnlySet<string> ResolvePermissions(PermissionSubject subject);

    bool IsAuthorized(
        PermissionSubject subject,
        string permission,
        string? resourceId = null);
}

public interface IDelegatedPermissionGrantIssuer
{
    string Issue(PermissionSubject subject, string? audience = null);

    string IssueRestricted(
        PermissionSubject subject,
        IEnumerable<string> permissions,
        string? audience = null);
}
