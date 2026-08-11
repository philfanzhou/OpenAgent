using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using OpenAgent.Authorization;

namespace OpenAgent.Hosting.Authorization;

internal sealed record GatewayPermissionRequirement(string Permission) : IAuthorizationRequirement;

internal sealed class GatewayPermissionAuthorizationHandler(
    IPermissionAuthorizationService authorization)
    : AuthorizationHandler<GatewayPermissionRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        GatewayPermissionRequirement requirement)
    {
        if (!GatewayClaimsIdentityMapper.TryMap(context.User, out AuthorizationSubject? subject))
        {
            return Task.CompletedTask;
        }

        AuthorizationDecision decision = authorization.Authorize(new(subject!, requirement.Permission));
        if (decision.IsAllowed
            || (requirement.Permission.Equals(
                    PermissionCatalog.AgentExecute,
                    StringComparison.OrdinalIgnoreCase)
                && PermissionMatcher.HasGrantForAnyResource(
                    decision.GrantedPermissions,
                    requirement.Permission)))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal static class GatewayClaimsIdentityMapper
{
    internal static bool TryMap(ClaimsPrincipal principal, out AuthorizationSubject? subject)
    {
        subject = null;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        string userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value
            ?? principal.Identity?.Name
            ?? "anonymous";
        string? tenantId = principal.FindFirst("tenant_id")?.Value
            ?? principal.FindFirst("tid")?.Value;
        IReadOnlyList<string> roles = principal.Claims
            .Where(claim => claim.Type == ClaimTypes.Role || claim.Type is "roles" or "role")
            .Select(claim => claim.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyList<string> groups = principal.Claims
            .Where(claim => claim.Type is "groups" or "group")
            .Select(claim => claim.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        IReadOnlyDictionary<string, string> claims = principal.Claims
            .GroupBy(claim => claim.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.Join(",", group.Select(claim => claim.Value)),
                StringComparer.OrdinalIgnoreCase);
        subject = new AuthorizationSubject(
            userId,
            tenantId,
            roles,
            groups,
            claims);
        return true;
    }
}
