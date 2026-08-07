using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Engine.Abstractions;

namespace OpenAgent.Engine.Redis;

internal class ConfigHealthCheck : IHealthCheck
{
    private readonly IRedisConnectionProvider _redis;

    public ConfigHealthCheck(IRedisConnectionProvider redis)
    {
        _redis = redis;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!_redis.IsAvailable)
            {
                return HealthCheckResult.Degraded(
                    "Redis is not available; running in fallback mode. In-memory configuration cache is optional.");
            }

            var agentKeys = await _redis.SetMembersAsync("agent:published:index");
            return HealthCheckResult.Healthy(
                $"Configuration store is available. Published agents: {agentKeys.Length}. In-memory configuration cache is optional.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "Failed to check config snapshot health.", ex);
        }
    }
}
