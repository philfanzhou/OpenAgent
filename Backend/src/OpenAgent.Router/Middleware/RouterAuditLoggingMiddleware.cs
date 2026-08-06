using System.Diagnostics;
using System.Text.Json;
using OpenAgent.Router.Observability;

namespace OpenAgent.Router.Middleware;

public class RouterAuditLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RouterAuditLoggingMiddleware> _logger;

    public RouterAuditLoggingMiddleware(RequestDelegate next, ILogger<RouterAuditLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;

        // Extract request details before processing
        var method = context.Request.Method;
        var path = context.Request.Path;
        var userId = context.User?.Identity?.Name ?? "anonymous";
        var tenantId = context.Request.Headers["X-Tenant-Id"].FirstOrDefault();

        string? query = null;
        if (context.Request.ContentLength > 0 && context.Request.HasFormContentType == false)
        {
            try
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
                var body = await reader.ReadToEndAsync();
                context.Request.Body.Position = 0;

                if (!string.IsNullOrEmpty(body))
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("query", out var q))
                        query = q.GetString();
                    else if (doc.RootElement.TryGetProperty("message", out var m))
                        query = m.GetString();
                }
            }
            catch (Exception ex)
            {
                RouterLog.AuditBodyParseFailed(_logger, ex, method, path, traceId);
            }
        }

        try
        {
            await _next(context);

            stopwatch.Stop();

            var statusCode = context.Response.StatusCode;
            var outcome = statusCode >= 200 && statusCode < 400 ? "Success" : "Failure";

            RouterLog.AuditRequestCompleted(_logger, traceId, method, path, userId, tenantId, query, statusCode, outcome, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            RouterLog.AuditRequestFailed(_logger, ex, traceId, method, path, userId, tenantId, query, stopwatch.ElapsedMilliseconds);

            throw;
        }
    }
}
