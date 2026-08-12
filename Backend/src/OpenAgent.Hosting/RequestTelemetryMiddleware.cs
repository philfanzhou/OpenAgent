using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Hosting;

internal sealed class RequestTelemetryMiddleware(
    RequestDelegate next,
    ILogger<RequestTelemetryMiddleware> logger)
{
    private const string MetricsScrapePath = "/metrics";

    /// <summary>
    /// 判断请求路径是否为 Prometheus scrape 端点。该端点会被频繁拉取，
    /// 不应进入 trace、metrics 标签或请求完成日志，以免淹没常规业务日志。
    /// </summary>
    internal static bool IsMetricsScrapePath(PathString path)
        => path.StartsWithSegments(MetricsScrapePath, StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsMetricsScrapePath(context.Request.Path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

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
