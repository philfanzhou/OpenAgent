using Microsoft.Extensions.Options;
using OpenAgent.Authorization;

namespace OpenAgent.Hosting.Authorization;

internal sealed class GatewayAuthorizationService(
    IOptions<GatewayAuthorizationOptions> options,
    GatewayGrantCodec codec,
    TimeProvider timeProvider) : IPermissionAuthorizationService, IDelegatedAuthorizationIssuer
{
    private readonly GatewayAuthorizationOptions _options = options.Value;

    public IReadOnlySet<string> GetPermissions(AuthorizationSubject subject)
    {
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

    public AuthorizationDecision Authorize(AuthorizationRequest request)
    {
        IReadOnlySet<string> permissions = GetPermissions(request.Subject);
        return new AuthorizationDecision(
            PermissionMatcher.IsAllowed(
                permissions,
                request.Permission,
                request.ResourceId),
            permissions);
    }

    public string Issue(DelegatedAuthorization authorization)
    {
        string[] restrictedPermissions = authorization.Permissions
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        long issuedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        GatewayGrantPayload payload = new()
        {
            Issuer = _options.Issuer,
            Audience = authorization.Audience ?? _options.Audience,
            Subject = authorization.Subject.SubjectId,
            TenantId = authorization.Subject.TenantId,
            Roles = authorization.Subject.Roles,
            Groups = authorization.Subject.Groups,
            Permissions = restrictedPermissions,
            IssuedAt = issuedAt,
            ExpiresAt = issuedAt + _options.GrantLifetimeSeconds,
            TokenId = Guid.NewGuid().ToString("N")
        };
        return codec.Encode(payload);
    }

}
