using OpenAgent.Authorization;

namespace OpenAgent.Router.Tests;

internal sealed class TestPermissionServices(
    bool allow = true,
    string grant = "test-gateway-grant",
    Func<string, string?, bool>? evaluator = null) : IPermissionAuthorizer, IDelegatedPermissionGrantIssuer
{
    public IReadOnlyList<string>? RestrictedPermissions { get; private set; }

    public IReadOnlySet<string> ResolvePermissions(PermissionSubject subject) =>
        allow
            ? new HashSet<string>(["*"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool IsAuthorized(
        PermissionSubject subject,
        string permission,
        string? resourceId = null) => allow
            && subject.IsAuthenticated
            && (evaluator?.Invoke(permission, resourceId) ?? true);

    public string Issue(
        PermissionSubject subject,
        string? audience = null) => grant;

    public string IssueRestricted(
        PermissionSubject subject,
        IEnumerable<string> permissions,
        string? audience = null)
    {
        RestrictedPermissions = permissions.ToArray();
        return grant;
    }
}
