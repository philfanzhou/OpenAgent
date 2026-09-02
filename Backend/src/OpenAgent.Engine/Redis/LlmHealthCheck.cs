using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Observability;

namespace OpenAgent.Engine.Redis;

internal class LlmHealthCheck : IHealthCheck
{
    private readonly IAgentConfigProvider _configProvider;
    private readonly ILogger<LlmHealthCheck> _logger;

    public LlmHealthCheck(IAgentConfigProvider configProvider, ILogger<LlmHealthCheck> logger)
    {
        _configProvider = configProvider;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<AgentSummary> agents = await _configProvider
                .ListAgentsAsync(cancellationToken)
                .ConfigureAwait(false);

            if (agents.Count == 0)
            {
                return HealthCheckResult.Degraded(
                    "No Agent configurations found in PostgreSQL.");
            }

            AgentSummary sample = agents[0];
            AgentConfig? config = await _configProvider.GetConfigAsync(
                    sample.AgentId,
                    sample.TenantId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (config?.Llm == null)
            {
                return HealthCheckResult.Unhealthy(
                    $"No LLM configuration available for agent '{sample.AgentId}'.");
            }

            return HealthCheckResult.Healthy(
                $"ApiFormat: {config.Llm.Format}, Model: {config.Llm.ModelId} (verified via agent '{sample.AgentId}')");
        }
        catch (Exception ex)
        {
            EngineLog.LlmHealthCheckFailed(_logger, ex);
            return HealthCheckResult.Degraded(
                "Unable to retrieve LLM configuration.", ex);
        }
    }
}
