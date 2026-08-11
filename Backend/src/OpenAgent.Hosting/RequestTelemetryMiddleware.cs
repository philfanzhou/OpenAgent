using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Hosting;

internal sealed class RequestTelemetryMiddleware(
    RequestDelegate next,
    ILogger<RequestTelemetryMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        long started = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            double durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            string route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                ?? "unmatched";
            string method = context.Request.Method;
            int statusCode = failure == null
                ? context.Response.StatusCode
                : StatusCodes.Status500InternalServerError;

            Activity? activity = Activity.Current;
            activity?.SetTag("openagent.route", route);
            activity?.SetTag(
                "openagent.agent.id",
                context.Response.Headers["X-OpenAgent-Selected-Agent-Id"].FirstOrDefault());
            if (failure == null)
            {
                logger.LogInformation(
                    "Request completed. Method={Method}, Route={Route}, StatusCode={StatusCode}, DurationMs={DurationMs}",
                    method,
                    route,
                    statusCode,
                    durationMs);
            }
            else
            {
                logger.LogError(
                    failure,
                    "Request failed. Method={Method}, Route={Route}, StatusCode={StatusCode}, DurationMs={DurationMs}",
                    method,
                    route,
                    statusCode,
                    durationMs);
            }
        }
    }
}
