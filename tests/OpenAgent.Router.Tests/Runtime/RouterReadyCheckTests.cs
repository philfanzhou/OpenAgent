using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OpenAgent.Router.Tests.Runtime;

public class RouterReadyCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_DownstreamUnreachable_ReturnsUnhealthy()
    {
        RouterReadyCheck check = new(
            new SequenceRouteTable("http://engine:5208"),
            new StubProbe([]),
            new StubHealthTracker(),
            NullLogger<RouterReadyCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("not serviceable", result.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckHealthAsync_DynamicUnreachableAndFallbackReady_ReturnsDegraded()
    {
        SequenceRouteTable routes = new(
            "http://dynamic-engine:5208",
            "http://static-engine:5208");
        RouterReadyCheck check = new(
            routes,
            new StubProbe(["http://static-engine:5208"]),
            new StubHealthTracker(),
            NullLogger<RouterReadyCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("fallback", result.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckHealthAsync_DownstreamReady_ReturnsHealthy()
    {
        RouterReadyCheck check = new(
            new SequenceRouteTable("http://engine:5208"),
            new StubProbe(["http://engine:5208"]),
            new StubHealthTracker(),
            NullLogger<RouterReadyCheck>.Instance);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    private sealed class SequenceRouteTable(params string?[] endpoints) : IRouteTable
    {
        private int _index;

        public string? GetTargetEndpoint(string intent)
        {
            int index = Math.Min(Interlocked.Increment(ref _index) - 1, endpoints.Length - 1);
            return endpoints[index];
        }
    }

    private sealed class StubProbe(IReadOnlyCollection<string> readyEndpoints) : IEngineReadinessProbe
    {
        public Task<bool> IsReadyAsync(
            string endpoint,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(readyEndpoints.Contains(endpoint, StringComparer.OrdinalIgnoreCase));
    }

    private sealed class StubHealthTracker : IEndpointHealthTracker
    {
        public bool IsAvailable(string endpoint) => true;
        public void ReportSuccess(string endpoint) { }
        public void ReportFailure(string endpoint) { }
    }
}
