using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Routing;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Runtime;

namespace OpenAgent.Engine.Host.Middleware;

internal sealed class EngineAdmissionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ShutdownService shutdown)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1/agent"))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        AgentRequestFeature request = context.GetAgentRequest();
        if (context.Request.Path.StartsWithSegments("/api/v1/agent/chat"))
        {
            EnsureChatAccess(
                request.User,
                context.Request.Headers[AgentRoutingHeaders.ResolvedAgentId].FirstOrDefault());
        }
        using RequestScope scope = new(shutdown, context.Request.Path, request.TraceId);
        await next(context).ConfigureAwait(false);
    }

    internal static void EnsureChatAccess(IAgentUserContext user, string? agentId)
    {
        if (!user.IsAuthenticated)
        {
            throw new AgentException(AgentErrorCode.PermissionDenied, "User is not authenticated");
        }

        if (string.IsNullOrWhiteSpace(user.TenantId))
        {
            throw new TenantDataIsolationException(null, null, "TenantId is required but not provided");
        }

        if (!string.IsNullOrWhiteSpace(agentId)
            && !GatewayPermissionMatcher.IsAllowed(
                GatewayPermissionMatcher.ReadPermissions(user.Claims),
                GatewayPermissions.AgentExecute,
                agentId))
        {
            throw new AgentException(
                AgentErrorCode.PermissionDenied,
                $"Access denied for Agent resource '{agentId ?? "unknown"}'");
        }
    }
}
