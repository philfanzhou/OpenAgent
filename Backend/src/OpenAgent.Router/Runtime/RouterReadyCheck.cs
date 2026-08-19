using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Router.Observability;
using StackExchange.Redis;

namespace OpenAgent.Router;

internal sealed class RouterReadyCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly IRouteTable _routeTable;
    private readonly IEngineReadinessProbe _probe;
    private readonly IEndpointHealthTracker _healthTracker;
    private readonly ILogger<RouterReadyCheck> _logger;

    public RouterReadyCheck(
        IRouteTable routeTable,
        IEngineReadinessProbe probe,
        IEndpointHealthTracker healthTracker,
        ILogger<RouterReadyCheck> logger,
        IConnectionMultiplexer? redis = null)
    {
        _redis = redis;
        _routeTable = routeTable;
        _probe = probe;
        _healthTracker = healthTracker;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        Dictionary<string, object> checks = [];
        bool redisDegraded = await CheckRedisAsync(checks, cancellationToken)
            .ConfigureAwait(false);
        string? endpoint = _routeTable.GetTargetEndpoint("chat");
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            checks["engine"] = "No Engine endpoint is available";
            RouterMeter.RecordDownstreamHealth("engine", "unavailable");
            return HealthCheckResult.Unhealthy("Router has no serviceable downstream", data: checks);
        }

        if (await _probe.IsReadyAsync(endpoint, cancellationToken).ConfigureAwait(false))
        {
            _healthTracker.ReportSuccess(endpoint);
            checks["engine"] = $"Engine is ready: {endpoint}";
            RouterMeter.RecordDownstreamHealth("engine", "healthy");
            return redisDegraded
                ? HealthCheckResult.Degraded("Router is serving through a degraded discovery path", data: checks)
                : HealthCheckResult.Healthy("Router is ready", data: checks);
        }

        _healthTracker.ReportFailure(endpoint);
        RouterMeter.RecordDownstreamHealth("engine", "unavailable");
        RouterLog.DownstreamQuarantined(_logger, endpoint);
        string? fallbackEndpoint = _routeTable.GetTargetEndpoint("chat");
        if (!string.IsNullOrWhiteSpace(fallbackEndpoint) &&
            !string.Equals(endpoint, fallbackEndpoint, StringComparison.OrdinalIgnoreCase) &&
            await _probe.IsReadyAsync(fallbackEndpoint, cancellationToken).ConfigureAwait(false))
        {
            _healthTracker.ReportSuccess(fallbackEndpoint);
            RouterMeter.RecordDownstreamHealth("engine", "healthy");
            RouterLog.ReadinessFallbackSelected(_logger, endpoint, fallbackEndpoint);
            checks["engine"] = $"Fallback Engine is ready: {fallbackEndpoint}";
            return HealthCheckResult.Degraded(
                "Router is serving through a fallback downstream", data: checks);
        }

        checks["engine"] = $"Engine is unreachable: {endpoint}";
        return HealthCheckResult.Unhealthy("Router downstream is not serviceable", data: checks);
    }

    private async Task<bool> CheckRedisAsync(
        IDictionary<string, object> checks,
        CancellationToken cancellationToken)
    {
        if (_redis == null)
        {
            checks["redis"] = "Redis is not configured; static discovery is active";
            RouterMeter.RecordDownstreamHealth("redis", "not_configured");
            return false;
        }

        try
        {
            await _redis.GetDatabase().PingAsync()
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            checks["redis"] = "Redis connection is healthy";
            RouterMeter.RecordDownstreamHealth("redis", "healthy");
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RouterLog.RedisPingFailedDuringReadinessCheck(_logger, ex);
            checks["redis"] = "Redis connection is degraded";
            RouterMeter.RecordDownstreamHealth("redis", "degraded");
            return true;
        }
    }
}
