using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace OpenAgent.Hosting;

internal sealed class RequestTelemetryMiddleware(
    RequestDelegate next,
    ILogger<RequestTelemetryMiddleware> logger)
{
    private static readonly Meter Meter = new("OpenAgent.Hosting");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "openagent.requests");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "openagent.request.duration",
        "ms");

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
            TagList tags = new()
            {
                { "http.request.method", method },
                { "http.route", route },
                { "http.response.status_code", statusCode }
            };
            Requests.Add(1, tags);
            Duration.Record(durationMs, tags);

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
