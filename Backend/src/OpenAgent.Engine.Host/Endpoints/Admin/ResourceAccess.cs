using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;
using OpenAgent.Engine.Hosting.Errors;

namespace OpenAgent.Engine.Host.Endpoints.Admin;

internal static class ResourceAccess
{
    internal static bool HasScope(IAgentUserContext user, string scope) => user.IsAuthenticated
        && !string.IsNullOrWhiteSpace(user.TenantId)
        && (user.Roles.Contains("Admin", StringComparer.OrdinalIgnoreCase)
            || user.Claims.Where(item => item.Key is "scope" or "scp" or "permissions")
                .SelectMany(item => item.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Any(value => string.Equals(value, "agent.admin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, scope, StringComparison.OrdinalIgnoreCase)));

    internal static async Task<IReadOnlyList<T>> FilterAsync<T>(IReadOnlyList<T> items,
        Func<T, string> id, AgentResourceType type, string action, IResourceAuthorization authorization,
        HttpContext context, CancellationToken ct)
    {
        List<T> visible = [];
        foreach (T item in items)
        {
            string resourceId = id(item);
            if (await authorization.IsAuthorizedAsync(new(type == AgentResourceType.Agent ? resourceId : string.Empty,
                type, resourceId, action), context.GetAgentRequest().User, ct).ConfigureAwait(false)) visible.Add(item);
        }
        return visible;
    }
}

