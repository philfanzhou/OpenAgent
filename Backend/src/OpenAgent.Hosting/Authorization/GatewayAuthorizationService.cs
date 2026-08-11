using Microsoft.Extensions.Options;
using OpenAgent.Authorization;

namespace OpenAgent.Hosting.Authorization;

internal sealed class GatewayAuthorizationService(
    IOptions<GatewayAuthorizationOptions> options,
    GatewayGrantCodec codec,
    TimeProvider timeProvider) : IPermissionAuthorizer, IDelegatedPermissionGrantIssuer
{
    private readonly GatewayAuthorizationOptions _options = options.Value;

    public IReadOnlySet<string> ResolvePermissions(PermissionSubject subject)
    {
        if (!subject.IsAuthenticated)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        HashSet<string> permissions = new(
            _options.AuthenticatedPermissions,
            StringComparer.OrdinalIgnoreCase);
        permissions.UnionWith(PermissionMatcher.ReadPermissions(subject.Claims));
        foreach (string role in subject.Roles)
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
        PermissionSubject subject,
        string permission,
        string? resourceId = null)
    {
        return subject.IsAuthenticated
            && PermissionMatcher.IsAllowed(
                ResolvePermissions(subject),
                permission,
                resourceId);
    }

    public string Issue(
        PermissionSubject subject,
        string? audience = null)
    {
        return IssueRestricted(subject, ResolvePermissions(subject), audience);
    }

    public string IssueRestricted(
        PermissionSubject subject,
        IEnumerable<string> permissions,
        string? audience = null)
    {
        if (!subject.IsAuthenticated)
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
            Subject = subject.SubjectId,
            TenantId = subject.TenantId,
            Roles = subject.Roles,
            Groups = subject.Groups,
            Permissions = restrictedPermissions,
            IssuedAt = issuedAt,
            ExpiresAt = issuedAt + _options.GrantLifetimeSeconds,
            TokenId = Guid.NewGuid().ToString("N")
        };
        return codec.Encode(payload);
    }

}
