using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Middleware;

internal sealed class RateLimitingMiddleware(RequestDelegate next, ILogger<RateLimitingMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        IAgentUserContext userContext,
        IRateLimiter rateLimiter)
    {
        if (!userContext.IsAuthenticated)
        {
            await next(context);
            return;
        }

        var tenantId = context.Items[TenantIsolationMiddleware.TenantItemKey]?.ToString();
        var clientId = $"{tenantId}:{userContext.UserId}";
        if (!await rateLimiter.IsAllowedAsync(clientId, context.RequestAborted))
        {
            RouterLog.RateLimited(
                logger, TenantIsolationMiddleware.GetAction(context), clientId,
                userContext.UserId, tenantId, Activity.Current?.Id ?? context.TraceIdentifier);
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }

        await next(context);
    }
}
