using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authorization;

namespace OpenAgent.Router.Tests;

internal sealed class TestGatewayAuthorizationService(
    bool allow = true,
    string grant = "test-gateway-grant",
    Func<string, string?, bool>? evaluator = null) : IGatewayAuthorizationService
{
    public IReadOnlyList<string>? RestrictedPermissions { get; private set; }

    public IReadOnlySet<string> ResolvePermissions(GatewayIdentity identity) =>
        allow
            ? new HashSet<string>(["*"], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool IsAuthorized(
        GatewayIdentity identity,
        string requiredPermission,
        string? resourceId = null) => allow
            && identity.IsAuthenticated
            && (evaluator?.Invoke(requiredPermission, resourceId) ?? true);

    public string IssueGrant(
        GatewayIdentity identity,
        string? audience = null) => grant;

    public string IssueRestrictedGrant(
        GatewayIdentity identity,
        IEnumerable<string> permissions,
        string? audience = null)
    {
        RestrictedPermissions = permissions.ToArray();
        return grant;
    }
}
