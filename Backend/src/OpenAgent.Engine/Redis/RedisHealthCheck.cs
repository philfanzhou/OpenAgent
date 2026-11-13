using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Engine.Abstractions;

namespace OpenAgent.Engine.Redis;

internal class RedisHealthCheck : IHealthCheck
{
    private readonly IRedisConnectionProvider _redis;

    public RedisHealthCheck(IRedisConnectionProvider redis)
    {
        _redis = redis;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_redis.IsAvailable)
        {
            return HealthCheckResult.Degraded("Redis connection not available - running in fallback mode");
        }

        try
        {
            await _redis.PingAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Redis connection is healthy");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return HealthCheckResult.Unhealthy("Redis connection failed");
        }
    }
}
