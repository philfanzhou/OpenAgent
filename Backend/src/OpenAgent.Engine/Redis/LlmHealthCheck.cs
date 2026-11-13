using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Models;

namespace OpenAgent.Engine.Redis;

internal sealed class LlmHealthCheck(
    ILlmConfigRepository repository,
    IAgentConfigRepository agentRepository) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<LlmProviderProfile> profiles = await repository
                .ListAsync(null, cancellationToken).ConfigureAwait(false);
            IReadOnlyList<AgentConfigEntity> agents = await agentRepository
                .ListAsync(null, cancellationToken).ConfigureAwait(false);
            int validProviderCount = profiles.Count(ModelConfigValidation.IsValid);
            int invalidProviderCount = profiles.Count - validProviderCount;
            int inlineModelCount = agents.Count(agent => agent.Config.OpenCode.Model is not null);
            int invalidInlineModelCount = agents.Count(agent =>
                agent.Config.OpenCode.Model is not null && !ModelConfigValidation.IsValid(agent.Config.OpenCode.Model));
            Dictionary<string, object> data = new()
            {
                ["providerCount"] = profiles.Count,
                ["validProviderCount"] = validProviderCount,
                ["invalidProviderCount"] = invalidProviderCount,
                ["inlineModelCount"] = inlineModelCount,
                ["invalidInlineModelCount"] = invalidInlineModelCount
            };

            if (invalidProviderCount > 0 || invalidInlineModelCount > 0)
            {
                return HealthCheckResult.Unhealthy("LLM configuration contains invalid data.", data: data);
            }

            if (validProviderCount == 0 && inlineModelCount == 0)
            {
                return HealthCheckResult.Degraded("No LLM provider is configured.", data: data);
            }

            return HealthCheckResult.Healthy("LLM provider configuration is available.", data);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("Failed to read LLM provider configuration.", exception);
        }
    }
}
