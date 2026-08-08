using Microsoft.Extensions.Options;

namespace OpenAgent.Hosting.Authorization;

internal sealed class GatewayAuthorizationService(
    IOptions<GatewayAuthorizationOptions> options,
    GatewayGrantCodec codec,
    TimeProvider timeProvider) : IGatewayAuthorizationService
{
    private readonly GatewayAuthorizationOptions _options = options.Value;

    public IReadOnlySet<string> ResolvePermissions(GatewayIdentity identity)
    {
        if (!identity.IsAuthenticated)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        HashSet<string> permissions = new(
            _options.AuthenticatedPermissions,
            StringComparer.OrdinalIgnoreCase);
        permissions.UnionWith(ReadPermissions(identity.Claims));
        foreach (string role in identity.Roles)
        {
            KeyValuePair<string, List<string>> roleGrant = _options.RolePermissions
                .FirstOrDefault(item => item.Key.Equals(role, StringComparison.OrdinalIgnoreCase));
            if (roleGrant.Value != null)
            {
                permissions.UnionWith(roleGrant.Value);
            }
        }

        return permissions;
    }

    public bool IsAuthorized(
        GatewayIdentity identity,
        string requiredPermission,
        string? resourceId = null)
    {
        return identity.IsAuthenticated
            && IsAllowed(
                ResolvePermissions(identity),
                requiredPermission,
                resourceId);
    }

    public string IssueGrant(
        GatewayIdentity identity,
        string? audience = null)
    {
        return IssueRestrictedGrant(identity, ResolvePermissions(identity), audience);
    }

    public string IssueRestrictedGrant(
        GatewayIdentity identity,
        IEnumerable<string> permissions,
        string? audience = null)
    {
        if (!identity.IsAuthenticated)
        {
            throw new InvalidOperationException("An authenticated identity is required to issue a gateway grant.");
        }

        string[] restrictedPermissions = permissions
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        long issuedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        GatewayGrantPayload payload = new()
        {
            Issuer = _options.Issuer,
            Audience = audience ?? _options.Audience,
            Subject = identity.UserId,
            TenantId = identity.TenantId,
            Roles = identity.Roles,
            Groups = identity.Groups,
            Permissions = restrictedPermissions,
            IssuedAt = issuedAt,
            ExpiresAt = issuedAt + _options.GrantLifetimeSeconds,
            TokenId = Guid.NewGuid().ToString("N")
        };
        return codec.Encode(payload);
    }

    private static IEnumerable<string> ReadPermissions(
        IReadOnlyDictionary<string, string> claims)
    {
        return claims
            .Where(claim => claim.Key.Equals(GatewayAuthorizationDefaults.PermissionClaimType, StringComparison.OrdinalIgnoreCase)
                || claim.Key.Equals("permissions", StringComparison.OrdinalIgnoreCase)
                || claim.Key.Equals("scope", StringComparison.OrdinalIgnoreCase)
                || claim.Key.Equals("scp", StringComparison.OrdinalIgnoreCase))
            .SelectMany(claim => claim.Value.Split(
                [' ', ','],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static bool IsAllowed(
        IEnumerable<string> grantedPermissions,
        string requiredPermission,
        string? resourceId)
    {
        HashSet<string> permissions = grantedPermissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (permissions.Contains("*") || permissions.Contains(requiredPermission))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(resourceId)
            && (permissions.Contains($"{requiredPermission}:*")
                || permissions.Contains($"{requiredPermission}:{resourceId}"));
    }
}
