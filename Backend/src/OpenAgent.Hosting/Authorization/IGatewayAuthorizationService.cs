namespace OpenAgent.Hosting.Authorization;

public sealed record GatewayIdentity(
    string UserId,
    string? TenantId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Groups,
    IReadOnlyDictionary<string, string> Claims,
    bool IsAuthenticated);

public interface IGatewayAuthorizationService
{
    IReadOnlySet<string> ResolvePermissions(GatewayIdentity identity);

    bool IsAuthorized(
        GatewayIdentity identity,
        string requiredPermission,
        string? resourceId = null);

    string IssueGrant(
        GatewayIdentity identity,
        string? audience = null);

    string IssueRestrictedGrant(
        GatewayIdentity identity,
        IEnumerable<string> permissions,
        string? audience = null);
}
