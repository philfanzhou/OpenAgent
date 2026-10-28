using OpenAgent.Engine.Runtime;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

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
            EnsureChatAccess(request.User);
        }
        using RequestScope scope = new(shutdown, context.Request.Path, request.TraceId);
        await next(context).ConfigureAwait(false);
    }

    private static void EnsureChatAccess(IAgentUserContext user)
    {
        if (!user.IsAuthenticated)
        {
            throw new AgentException(AgentErrorCode.PermissionDenied, "User is not authenticated");
        }

        if (string.IsNullOrWhiteSpace(user.TenantId))
        {
            throw new TenantDataIsolationException(null, null, "TenantId is required but not provided");
        }
    }
}
