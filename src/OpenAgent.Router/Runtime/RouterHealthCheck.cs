using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace OpenAgent.Router;

public class RouterHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(HealthCheckResult.Healthy("Router is alive"));
    }
}
