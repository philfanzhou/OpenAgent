using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal static class EngineRequestIdentityExtensions
{
    internal static void ApplyTo(
        this EngineRequestIdentity identity,
        HttpRequestMessage request)
    {
        AddHeader(request, "Authorization", identity.Authorization);
        AddHeader(request, "X-Tenant-Id", identity.TenantId);
        AddHeader(request, "X-Agent-Audience", identity.AgentAudience);
    }

    private static void AddHeader(
        HttpRequestMessage request,
        string name,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
