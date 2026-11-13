using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;

namespace OpenAgent.Engine.Redis;

internal sealed class ConfigHealthCheck(IAgentConfigRepository repository) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<AgentConfigEntity> agents = await repository
                .ListAsync(null, cancellationToken)
                .ConfigureAwait(false);
            int publishedAgentCount = agents.Count(agent => agent.Status == AgentPublishStatus.Published);
            int invalidAgentCount = agents.Count(agent => !IsValid(agent));
            Dictionary<string, object> data = new()
            {
                ["agentCount"] = agents.Count,
                ["publishedAgentCount"] = publishedAgentCount,
                ["invalidAgentCount"] = invalidAgentCount
            };

            if (invalidAgentCount > 0)
            {
                return HealthCheckResult.Unhealthy("Agent configuration contains invalid data.", data: data);
            }

            if (agents.Count == 0)
            {
                return HealthCheckResult.Degraded("No agent configuration is available.", data: data);
            }

            return HealthCheckResult.Healthy("Agent configuration is available.", data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Failed to read agent configuration.", exception);
        }
    }

    private static bool IsValid(AgentConfigEntity agent)
    {
        if (string.IsNullOrWhiteSpace(agent.TenantId)
            || string.IsNullOrWhiteSpace(agent.AgentId)
            || !string.Equals(agent.TenantId, agent.Config.TenantId, StringComparison.Ordinal)
            || agent.Config.MaxTurns < 0)
        {
            return false;
        }

        OpenCodeModelConfig? model = agent.Config.OpenCode.Model;
        if (model is null)
        {
            return true;
        }

        return ModelConfigValidation.IsValid(model);
    }
}
