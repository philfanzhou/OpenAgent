using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OpenAgent.Hosting;

internal sealed class RequestTelemetryMiddleware(
    RequestDelegate next,
    IOptions<AgentHostOptions> hostOptions,
    ILogger<RequestTelemetryMiddleware> logger)
{
    private const string MetricsScrapePath = "/metrics";
    private const string HealthPathPrefix = "/health";

    /// <summary>
    /// 判断请求路径是否为 Prometheus scrape 端点。该端点会被频繁拉取，
    /// 不应进入 trace、metrics 标签或请求完成日志，以免淹没常规业务日志。
    /// </summary>
    internal static bool IsMetricsScrapePath(PathString path)
        => path.StartsWithSegments(MetricsScrapePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 判断请求路径是否为健康探针端点（<c>/health*</c> 前缀覆盖 <c>/health/live</c>、
    /// <c>/health/ready</c>、<c>/health/report</c> 等别名，另含可配置的 live/ready 路径）。
    /// 编排器与 Router 会按秒级轮询这些端点，成功的探测不应进入 trace 或请求完成日志。
    /// </summary>
    internal static bool IsHealthProbePath(PathString path, AgentHostOptions options)
        => path.StartsWithSegments(HealthPathPrefix, StringComparison.OrdinalIgnoreCase)
            || MatchesConfiguredPath(path, options.HealthCheckLivePath)
            || MatchesConfiguredPath(path, options.HealthCheckReadyPath);

    /// <summary>
    /// 判断出站请求 URI 是否指向下游健康探针端点（如 Router 对 Engine 的 readiness 轮询），
    /// 用于在 HttpClient instrumentation 中过滤这类高频探测 span。
    /// </summary>
    internal static bool IsHealthProbeUri(Uri? requestUri, AgentHostOptions options)
    {
        if (requestUri is null || !requestUri.IsAbsoluteUri)
        {
            return false;
        }

        return IsHealthProbePath(PathString.FromUriComponent(requestUri), options);
    }

    private static bool MatchesConfiguredPath(PathString path, string configured)
        => !string.IsNullOrWhiteSpace(configured)
            && path.StartsWithSegments(new PathString(configured), StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsMetricsScrapePath(context.Request.Path))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (IsHealthProbePath(context.Request.Path, hostOptions.Value))
        {
            await InvokeHealthProbeAsync(context).ConfigureAwait(false);
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

    /// <summary>
    /// 健康探针不计时、不打 trace tag、成功时不产生任何日志；只有不健康（4xx/5xx）或
    /// 抛异常的探测才保留日志，避免高频轮询淹没业务日志，同时不丢失故障信号。
    /// </summary>
    private async Task InvokeHealthProbeAsync(HttpContext context)
    {
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
            if (failure != null)
            {
                logger.LogError(
                    failure,
                    "Health probe failed. Method={Method}, Path={Path}, StatusCode={StatusCode}",
                    context.Request.Method,
                    context.Request.Path.Value,
                    StatusCodes.Status500InternalServerError);
            }
            else if (context.Response.StatusCode >= StatusCodes.Status400BadRequest)
            {
                logger.LogWarning(
                    "Health probe unhealthy. Method={Method}, Path={Path}, StatusCode={StatusCode}",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.Response.StatusCode);
            }
        }
    }
}
