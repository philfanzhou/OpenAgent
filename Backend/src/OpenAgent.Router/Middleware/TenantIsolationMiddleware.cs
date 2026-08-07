using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Middleware;

internal sealed class TenantIsolationMiddleware(RequestDelegate next, ILogger<TenantIsolationMiddleware> logger)
{
    internal const string TenantItemKey = "RouterTenantId";

    public async Task InvokeAsync(HttpContext context, IAgentUserContext userContext)
    {
        if (!userContext.IsAuthenticated)
        {
            await next(context);
            return;
        }

        var headerTenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(headerTenantId)
            && userContext.TenantId != null
            && headerTenantId != userContext.TenantId)
        {
            RouterLog.TenantMismatchRejected(
                logger, GetAction(context), userContext.UserId, userContext.TenantId,
                headerTenantId, Activity.Current?.Id ?? context.TraceIdentifier);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        context.Items[TenantItemKey] = userContext.TenantId ?? headerTenantId;
        await next(context);
    }

    internal static string? GetAction(HttpContext context)
    {
        var prefix = "/api/v1/agent/chat/";
        var path = context.Request.Path.Value;
        return path?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true
            ? path[prefix.Length..]
            : null;
    }
}
