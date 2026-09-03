using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Abstractions;

namespace OpenAgent.Engine.Redis;

internal class ConfigHealthCheck : IHealthCheck
{
    private readonly IRedisConnectionProvider _redis;
    private readonly IAgentConfigRepository _repository;

    public ConfigHealthCheck(
        IRedisConnectionProvider redis,
        IAgentConfigRepository repository)
    {
        _redis = redis;
        _repository = repository;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<OpenAgent.Contracts.Models.AgentConfigEntity> agents = await _repository
                .ListAsync(tenantId: null, cancellationToken)
                .ConfigureAwait(false);
            if (!_redis.IsAvailable)
            {
                return HealthCheckResult.Degraded(
                    $"PostgreSQL configuration store is available with {agents.Count} agents; Redis cache is unavailable.");
            }

            return HealthCheckResult.Healthy(
                $"PostgreSQL configuration store and Redis cache are available. Agents: {agents.Count}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "Failed to check configuration store health.", ex);
        }
    }
}
