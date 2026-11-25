using System.Security.Claims;

namespace OpenAgent.Contracts.Security;

public static class AgentRoleClaims
{
    public static List<string> Read(ClaimsPrincipal? principal) => principal?.Claims
        .Where(claim => claim.Type is ClaimTypes.Role or "roles" or "role")
        .Select(claim => claim.Value)
        .Where(role => !string.IsNullOrWhiteSpace(role))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList() ?? [];
}
