using System.Diagnostics;
using Microsoft.Extensions.Caching.Distributed;
using OpenAgent.Contracts.Security;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Middleware;

internal sealed class IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        IAgentUserContext userContext,
        IDistributedCache distributedCache)
    {
        if (!userContext.IsAuthenticated)
        {
            await next(context);
            return;
        }

        var key = context.Request.Headers["Idempotency-Key"].FirstOrDefault();
        if (!string.IsNullOrEmpty(key))
        {
            try
            {
                var response = await distributedCache.GetStringAsync(
                    $"idempotency:{key}", context.RequestAborted);
                if (!string.IsNullOrEmpty(response))
                {
                    RouterLog.IdempotencyCacheHit(
                        logger, TenantIsolationMiddleware.GetAction(context), key,
                        userContext.UserId,
                        context.Items[TenantIsolationMiddleware.TenantItemKey]?.ToString(),
                        Activity.Current?.Id ?? context.TraceIdentifier);
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(response, context.RequestAborted);
                    return;
                }
            }
            catch (Exception exception)
            {
                RouterLog.IdempotencyCacheCheckFailed(
                    logger, exception, TenantIsolationMiddleware.GetAction(context), key,
                    Activity.Current?.Id ?? context.TraceIdentifier);
            }
        }

        await next(context);
    }
}
