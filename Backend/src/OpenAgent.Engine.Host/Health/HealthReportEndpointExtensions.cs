using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

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
            return Results.Ok(new
            {
                status = report.Status.ToString(),
                service = environment.ApplicationName,
                totalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds),
                items = report.Entries.Select(entry => new
                {
                    key = entry.Key,
                    status = entry.Value.Status.ToString(),
                    detail = entry.Value.Description,
                    latencyMs = Math.Round(entry.Value.Duration.TotalMilliseconds),
                    data = entry.Value.Data
                })
            });
        });
    }
}
