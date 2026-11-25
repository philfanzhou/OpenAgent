using System.Diagnostics;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Hosting.Errors;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Middleware;

internal sealed class RateLimitingMiddleware(RequestDelegate next, ILogger<RateLimitingMiddleware> logger)
{
    internal static bool AppliesTo(HttpContext context) => context.Request.Path.StartsWithSegments("/api/v1/agent/chat")
        || context.Request.Path.StartsWithSegments("/api/v1/agent/conversations")
        || (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api/v1/agent/files"));

    public async Task InvokeAsync(
        HttpContext context,
        IAgentUserContext userContext,
        IRateLimiter rateLimiter)
    {
        var tenantId = userContext.TenantId;
        string clientId = !userContext.IsAuthenticated ? $"anonymous:{context.Connection.RemoteIpAddress}"
            : context.Request.Path.StartsWithSegments("/api/v1/agent/files") ? $"uploads:{tenantId}:{userContext.UserId}"
            : context.Request.Path.StartsWithSegments("/api/v1/agent/conversations") ? $"conversations:{tenantId}:{userContext.UserId}"
            : $"chat:{tenantId}:{userContext.UserId}";
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
