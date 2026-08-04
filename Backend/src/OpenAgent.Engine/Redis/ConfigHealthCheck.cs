using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Models;

namespace OpenAgent.Engine.Redis;

internal class ConfigHealthCheck : IHealthCheck
{
    private readonly ConfigSnapshot _snapshot;
    private readonly IRedisConnectionProvider _redis;

    public ConfigHealthCheck(ConfigSnapshot snapshot, IRedisConnectionProvider redis)
    {
        _snapshot = snapshot;
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
                    "Redis is not available; cannot verify config snapshot freshness against published agents.");
            }

            var agentKeys = await _redis.SetMembersAsync("agent:published:index");

            if (agentKeys.Length == 0)
            {
                return HealthCheckResult.Degraded(
                    "No published agents found in Redis.");
            }

            int snapshotHits = 0;
            var sampleAgentId = agentKeys.First().ToString();

            foreach (var agentKey in agentKeys)
            {
                var agentId = agentKey.ToString();
                if (_snapshot.TryGetConfig<AgentConfig>(agentId, "FullAgentConfig", out var config) && config != null)
                {
                    snapshotHits++;
                }
            }

            if (snapshotHits == agentKeys.Length)
            {
                return HealthCheckResult.Healthy(
                    $"Config snapshot fully populated. {snapshotHits}/{agentKeys.Length} agents cached.");
            }

            if (snapshotHits > 0)
            {
                return HealthCheckResult.Degraded(
                    $"Config snapshot partially populated. {snapshotHits}/{agentKeys.Length} agents cached. Sample agent: '{sampleAgentId}'.");
            }

            return HealthCheckResult.Unhealthy(
                $"Config snapshot is empty. 0/{agentKeys.Length} agents cached in snapshot.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "Failed to check config snapshot health.", ex);
        }
    }
}
