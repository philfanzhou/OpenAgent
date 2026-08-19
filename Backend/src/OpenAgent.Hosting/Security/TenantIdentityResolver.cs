using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Hosting.Security;

public static class TenantIdentityResolver
{
    public static string? Resolve(
        ClaimsPrincipal principal,
        IHeaderDictionary headers,
        bool allowDevelopmentHeader)
    {
        string? claimTenantId = ResolveSingle(
            principal.Claims
                .Where(claim => claim.Type is "tenant_id" or "tid")
                .Select(claim => claim.Value),
            "Authenticated identity contains conflicting tenant claims.");
        string? headerTenantId = ResolveDevelopmentHeader(headers);

        if (claimTenantId != null
            && headerTenantId != null
            && !string.Equals(claimTenantId, headerTenantId, StringComparison.Ordinal))
        {
            throw new AgentException(
                AgentErrorCode.TenantMismatch,
                "Authenticated tenant does not match X-Tenant-Id.");
        }

        return claimTenantId ?? (allowDevelopmentHeader ? headerTenantId : null);
    }

    public static string? ResolveDevelopmentHeader(IHeaderDictionary headers) => ResolveSingle(
        headers["X-Tenant-Id"].Concat(headers["X-TenantId"]),
        "Request contains conflicting tenant headers.");

    private static string? ResolveSingle(IEnumerable<string?> values, string conflictMessage)
    {
        string[] distinct = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (distinct.Length > 1)
        {
            throw new AgentException(AgentErrorCode.TenantMismatch, conflictMessage);
        }

        return distinct.SingleOrDefault();
    }
}
