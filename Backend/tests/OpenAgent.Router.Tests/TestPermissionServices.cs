using OpenAgent.Authorization;

namespace OpenAgent.Router.Tests;

internal sealed class TestPermissionServices(
    bool allow = true,
    string grant = "test-gateway-grant",
    Func<string, string?, bool>? evaluator = null) : IPermissionAuthorizationService, IDelegatedAuthorizationIssuer
{
    public IReadOnlyList<string>? RestrictedPermissions { get; private set; }

    public IReadOnlySet<string> GetPermissions(AuthorizationSubject subject) =>
        allow
            ? new HashSet<string>(["*"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public AuthorizationDecision Authorize(AuthorizationRequest request)
    {
        IReadOnlySet<string> permissions = GetPermissions(request.Subject);
        return new AuthorizationDecision(
            allow && (evaluator?.Invoke(request.Permission, request.ResourceId) ?? true),
            permissions);
    }

    public string Issue(DelegatedAuthorization authorization)
    {
        RestrictedPermissions = authorization.Permissions.ToArray();
        return grant;
    }
}
