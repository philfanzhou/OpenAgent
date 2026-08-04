using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Redis;

internal class LlmHealthCheck : IHealthCheck
{
    private readonly IAgentConfigProvider _configProvider;
    private readonly IRedisConnectionProvider _redis;
    private readonly ILogger<LlmHealthCheck> _logger;

    public LlmHealthCheck(IAgentConfigProvider configProvider, IRedisConnectionProvider redis, ILogger<LlmHealthCheck> logger)
    {
        _configProvider = configProvider;
        _redis = redis;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var agentKeys = await _redis.SetMembersAsync("agent:published:index");

            if (agentKeys.Length == 0)
            {
                return HealthCheckResult.Degraded(
                    "No published agents found in Redis.");
            }

            var sampleAgentId = agentKeys.First().ToString();
            var config = await _configProvider.GetConfigAsync(sampleAgentId, cancellationToken);

            if (config?.Llm == null)
            {
                return HealthCheckResult.Unhealthy(
                    $"No LLM configuration available for agent '{sampleAgentId}'.");
            }

            return HealthCheckResult.Healthy(
                $"ApiFormat: {config.Llm.Format}, Model: {config.Llm.ModelId} (verified via agent '{sampleAgentId}')");
        }
        catch (Exception ex)
        {
            EngineLog.LlmHealthCheckFailed(_logger, ex);
            return HealthCheckResult.Degraded(
                "Unable to retrieve LLM configuration.", ex);
        }
    }
}
