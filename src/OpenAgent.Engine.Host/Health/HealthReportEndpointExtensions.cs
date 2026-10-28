using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenAgent.Contracts.Responses;

namespace OpenAgent.Engine.Host.Health;

internal static class HealthReportEndpointExtensions
{
    public static IEndpointConventionBuilder MapHealthReport(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/health/report", async (
            HealthCheckService service,
            IHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            HealthReport report = await service.CheckHealthAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            return TypedResults.Ok(new HealthReportResponse
            {
                Status = report.Status.ToString(),
                Service = environment.ApplicationName,
                TotalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds),
                Items = report.Entries.Select(entry => new HealthReportItemResponse
                {
                    Key = entry.Key,
                    Status = entry.Value.Status.ToString(),
                    Detail = entry.Value.Description,
                    LatencyMs = Math.Round(entry.Value.Duration.TotalMilliseconds),
                    Data = entry.Value.Data
                }).ToList()
            });
        })
        .WithName("GetHealthReport")
        .WithTags("Health");
    }
}
