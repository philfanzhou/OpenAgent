using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Engine.Host.Health;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class HealthReportEndpointTests
{
    [Fact]
    public void MapHealthReport_MapsReportEndpoint()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddHealthChecks()
            .AddCheck("redis", () => HealthCheckResult.Healthy(), tags: ["live", "ready"]);

        var app = builder.Build();
        app.MapHealthReport();

        var routePatterns = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        Assert.Contains("/health/report", routePatterns);
    }
}
