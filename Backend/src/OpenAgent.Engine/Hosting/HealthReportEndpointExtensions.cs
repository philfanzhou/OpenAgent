using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Responses;

namespace OpenAgent.Engine.Hosting;

internal static class HealthReportEndpointExtensions
{
    public static IEndpointConventionBuilder MapHealthReport(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/health/report", async (
            HealthCheckService service,
            IOptions<AgentHostOptions> options,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            HealthReport report = await service.CheckHealthAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            context.Response.Headers.CacheControl = "no-store";
            return TypedResults.Ok(new HealthReportResponse
            {
                Status = report.Status.ToString(),
                Service = options.Value.ServiceName,
                TotalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds),
                Items = report.Entries.Select(entry => new HealthReportItemResponse
                {
                    Key = entry.Key,
                    Status = entry.Value.Status.ToString(),
                    Detail = entry.Value.Exception is null ? entry.Value.Description : "Health check failed.",
                    LatencyMs = Math.Round(entry.Value.Duration.TotalMilliseconds),
                    Data = entry.Value.Data
                }).ToList()
            });
        })
        .AllowAnonymous()
        .WithName("GetHealthReport")
        .WithTags("Health");
    }
}
