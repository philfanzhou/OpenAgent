using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;

namespace OpenAgent.Hosting;

public static class ApplicationBuilderExtensions
{
    public static IApplicationBuilder UseAgentHost(
        this IApplicationBuilder app,
        IConfiguration configuration,
        Action<AgentHostOptions>? configure = null)
    {
        var options = app.ApplicationServices.GetRequiredService<IOptions<AgentHostOptions>>().Value;

        if (options.EnableOpenTelemetry)
        {
            app.UseMiddleware<RequestTelemetryMiddleware>();
        }

        if (options.EnableCors)
        {
            app.UseCors(options.CorsPolicyName);
        }

        var env = app.ApplicationServices.GetRequiredService<IHostEnvironment>();
        if (options.EnableSwagger && env.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        if (options.EnableJwtAuth)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        if (options.EnableHealthChecks && app is IEndpointRouteBuilder endpoints)
        {
            MapHealthChecks(endpoints, options.HealthCheckLivePath, "live");
            MapHealthChecks(endpoints, options.HealthCheckReadyPath, "ready");

            if (!string.Equals(options.HealthCheckLivePath, "/health/live", StringComparison.OrdinalIgnoreCase))
            {
                MapHealthChecks(endpoints, "/health/live", "live");
            }

            if (!string.Equals(options.HealthCheckReadyPath, "/health/ready", StringComparison.OrdinalIgnoreCase))
            {
                MapHealthChecks(endpoints, "/health/ready", "ready");
            }
        }

        if (options.EnableOpenTelemetry
            && app is IEndpointRouteBuilder metricsEndpoints
            && app.ApplicationServices.GetService<MeterProvider>() != null)
        {
            metricsEndpoints.MapPrometheusScrapingEndpoint("/metrics");
        }

        return app;
    }

    private static void MapHealthChecks(IEndpointRouteBuilder endpoints, string path, string tag)
    {
        endpoints.MapHealthChecks(path, new HealthCheckOptions
        {
            Predicate = r => r.Tags.Contains(tag)
        });
    }
}
