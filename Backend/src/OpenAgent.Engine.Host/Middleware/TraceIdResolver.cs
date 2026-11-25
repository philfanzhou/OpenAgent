using Microsoft.AspNetCore.Http;
using OpenAgent.Engine.Hosting.Errors;

namespace OpenAgent.Engine.Host.Middleware;

/// <summary>
/// Unified TraceId resolution logic, ensuring all middleware in the Engine pipeline
/// (RequestContext / ExceptionHandler) uses the shared resolution logic.
/// Kept consistent with the callers of AgentRequestContextMiddleware.
/// </summary>
internal static class TraceIdResolver
{
    public static string Resolve(HttpContext context)
    {
        return AgentProblemDetails.ResolveTraceId(context);
    }
}
