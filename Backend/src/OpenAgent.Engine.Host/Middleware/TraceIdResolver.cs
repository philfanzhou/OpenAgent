using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace OpenAgent.Engine.Host.Middleware;

/// <summary>
/// Unified TraceId resolution logic, ensuring all middleware in the Engine pipeline
/// (RequestContext / ExceptionHandler) uses a consistent resolution order:
/// X-Trace-Id header → Activity.Current.Id → HttpContext.TraceIdentifier.
/// Kept consistent with the callers of AgentRequestContextMiddleware.
/// </summary>
internal static class TraceIdResolver
{
    public static string Resolve(HttpContext context)
    {
        return context.Request.Headers["X-Trace-Id"].FirstOrDefault()
            ?? Activity.Current?.Id
            ?? context.TraceIdentifier;
    }
}
