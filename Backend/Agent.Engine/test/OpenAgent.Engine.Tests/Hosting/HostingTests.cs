using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenAgent.Hosting;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class HostingTests
{
    [Fact]
    public void UseAgentHost_MapsLegacyHealthCheckAliases()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddHealthChecks()
            .AddCheck("live-check", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
            .AddCheck("ready-check", () => HealthCheckResult.Healthy(), tags: new[] { "ready" });
        builder.Services.Configure<AgentHostOptions>(options =>
        {
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableJwtAuth = false;
        });

        var app = builder.Build();
        app.UseAgentHost(builder.Configuration);

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        Assert.Contains("/health", routePatterns);
        Assert.Contains("/ready", routePatterns);
        Assert.Contains("/health/live", routePatterns);
        Assert.Contains("/health/ready", routePatterns);
    }

    [Fact]
    public void UseAgentHost_MapsPrometheusMetricsEndpoint()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddAgentHost(builder.Configuration, options =>
        {
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableJwtAuth = false;
            options.EnableHealthChecks = false;
            options.EnableOpenTelemetry = true;
        });

        var app = builder.Build();
        app.UseAgentHost(builder.Configuration);

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        Assert.Contains("/metrics", routePatterns);
    }
}
