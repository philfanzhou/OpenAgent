using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Router.Observability;
using StackExchange.Redis;

namespace OpenAgent.Router;

public class RouterReadyCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly IRouteTable _routeTable;
    private readonly ILogger<RouterReadyCheck> _logger;

    public RouterReadyCheck(IConnectionMultiplexer? redis, IRouteTable routeTable, ILogger<RouterReadyCheck> logger)
    {
        _redis = redis;
        _routeTable = routeTable;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var checks = new Dictionary<string, object>();

        // Check Redis if configured
        if (_redis != null)
        {
            try
            {
                var db = _redis.GetDatabase();
                await db.PingAsync();
                checks["redis"] = "Redis connection is healthy";
            }
            catch (Exception ex)
            {
                RouterLog.RedisPingFailedDuringReadinessCheck(_logger, ex);
                checks["redis"] = "Redis connection degraded";
            }
        }
        else
        {
            checks["redis"] = "Redis not configured - running in standalone mode";
        }

        // Check Engine endpoint availability
        var engineEndpoint = _routeTable.GetTargetEndpoint("chat");
        if (!string.IsNullOrEmpty(engineEndpoint))
        {
            checks["engine"] = $"Engine endpoint configured: {engineEndpoint}";
        }
        else
        {
            checks["engine"] = "No Engine endpoint available";
        }

        var hasUnhealthy = checks.ContainsValue("No Engine endpoint available");
        var hasDegraded = checks.ContainsValue("Redis connection degraded");

        if (hasUnhealthy)
        {
            return HealthCheckResult.Unhealthy("Router is not ready", data: checks);
        }

        if (hasDegraded)
        {
            return HealthCheckResult.Degraded("Router is degraded", data: checks);
        }

        return HealthCheckResult.Healthy("Router is ready", data: checks);
    }
}
