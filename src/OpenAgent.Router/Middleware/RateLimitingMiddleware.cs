using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Errors;
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

        var tenantId = userContext.TenantId;
        var clientId = $"{tenantId}:{userContext.UserId}";
        RateLimitDecision decision = await rateLimiter.AcquireAsync(
            clientId, context.RequestAborted).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            RouterLog.RateLimited(
                logger, RouterRequestMetadata.GetAction(context), clientId,
                userContext.UserId, tenantId, Activity.Current?.Id ?? context.TraceIdentifier);
            int retryAfterSeconds = Math.Max((int)Math.Ceiling(decision.RetryAfter.TotalSeconds), 1);
            context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            await AgentProblemDetails.WriteAsync(
                context,
                AgentProblemDetails.RateLimited(
                    $"Too many requests. Retry after {retryAfterSeconds} seconds.", context),
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await next(context);
    }
}
