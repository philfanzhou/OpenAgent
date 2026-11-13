using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Responses;
using OpenAgent.Engine.Hosting;
using Xunit;

namespace OpenAgent.Engine.Tests.Hosting;

public class HealthReportEndpointTests
{
    [Theory]
    [InlineData("openagent-engine", "/health", "/ready")]
    [InlineData("openagent-router", "/health/live", "/health/ready")]
    public async Task UseAgentHost_DependencyFailure_LeavesLivenessHealthyAndReadinessUnavailable(
        string serviceName, string livePath, string readyPath)
    {
        await using WebApplication app = await StartApplicationAsync(serviceName, HealthStatus.Unhealthy);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage live = await client.GetAsync(livePath);
        using HttpResponseMessage ready = await client.GetAsync(readyPath);
        using HttpResponseMessage report = await client.GetAsync("/health/report");
        HealthReportResponse body = await report.Content.ReadFromJsonAsync<HealthReportResponse>()
            ?? throw new InvalidOperationException("Health report was empty.");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Equal(serviceName, body.Service);
        Assert.Equal("Unhealthy", body.Status);
        HealthReportItemResponse dependency = Assert.Single(body.Items, item => item.Key == "dependency");
        Assert.Equal("Unhealthy", dependency.Status);
        Assert.Equal("Dependency is unavailable.", dependency.Detail);
        Assert.Equal(3, ((JsonElement)dependency.Data["count"]).GetInt32());
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, "Healthy")]
    [InlineData(HealthStatus.Degraded, "Degraded")]
    public async Task UseAgentHost_ServiceableDependency_ReturnsItsReadinessStatus(
        HealthStatus status, string expected)
    {
        await using WebApplication app = await StartApplicationAsync("openagent-engine", status);
        using HttpClient client = CreateClient(app);

        using HttpResponseMessage response = await client.GetAsync("/ready");
        HealthReportResponse report = await client.GetFromJsonAsync<HealthReportResponse>("/health/report")
            ?? throw new InvalidOperationException("Health report was empty.");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, report.Status);
    }

    [Fact]
    public async Task MapHealthReport_UnexpectedException_DoesNotExposeExceptionMessage()
    {
        await using WebApplication app = await StartApplicationAsync("openagent-engine", HealthStatus.Unhealthy,
            checks => checks.AddCheck("throwing", () => throw new InvalidOperationException("private-connection-string"),
                tags: ["ready"]));
        using HttpClient client = CreateClient(app);

        string json = await client.GetStringAsync("/health/report");
        HealthReportResponse report = JsonSerializer.Deserialize<HealthReportResponse>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Health report was empty.");

        Assert.Equal("Unhealthy", report.Status);
        HealthReportItemResponse failed = Assert.Single(report.Items, item => item.Key == "throwing");
        Assert.Equal("Unhealthy", failed.Status);
        Assert.Equal("Health check failed.", failed.Detail);
        Assert.DoesNotContain("private-connection-string", json);
    }

    [Fact]
    public async Task MapHealthReport_CancelledDependency_ReturnsUnhealthyInsteadOfHanging()
    {
        await using WebApplication app = await StartApplicationAsync("openagent-engine", HealthStatus.Healthy,
            checks => checks.AddCheck("slow", new CancellableHealthCheck(), tags: ["ready"],
                timeout: TimeSpan.FromMilliseconds(100)));
        using HttpClient client = CreateClient(app);
        client.Timeout = TimeSpan.FromSeconds(10);

        using HttpResponseMessage readiness = await client.GetAsync("/ready");
        HealthReportResponse report = await client.GetFromJsonAsync<HealthReportResponse>("/health/report")
            ?? throw new InvalidOperationException("Health report was empty.");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal("Unhealthy", report.Status);
        Assert.Equal("Unhealthy", Assert.Single(report.Items, item => item.Key == "slow").Status);
    }

    private static async Task<WebApplication> StartApplicationAsync(
        string serviceName, HealthStatus status, Action<IHealthChecksBuilder>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.Configure<AgentHostOptions>(options =>
        {
            options.ServiceName = serviceName;
            options.EnableCors = false;
            options.EnableSwagger = false;
            options.EnableJwtAuth = false;
            options.EnableOpenTelemetry = false;
        });
        IHealthChecksBuilder checks = builder.Services.AddHealthChecks()
            .AddCheck("process", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddCheck("dependency", () => new HealthCheckResult(status,
                "Dependency is unavailable.", data: new Dictionary<string, object> { ["count"] = 3 }), tags: ["ready"]);
        configure?.Invoke(checks);
        WebApplication app = builder.Build();
        app.UseAgentHost(builder.Configuration);
        await app.StartAsync();
        return app;
    }

    private static HttpClient CreateClient(WebApplication app)
    {
        IServer server = app.Services.GetRequiredService<IServer>();
        string address = server.Features.Get<IServerAddressesFeature>()?.Addresses.Single()
            ?? throw new InvalidOperationException("Server address was unavailable.");
        return new HttpClient { BaseAddress = new Uri(address) };
    }

    private sealed class CancellableHealthCheck : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return HealthCheckResult.Healthy();
        }
    }
}
