using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Configuration;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Engine.Redis;

/// <summary>
/// Checks that at least one structurally valid LLM provider profile is readable.
/// It deliberately does not call an external model endpoint from a health probe.
/// </summary>
internal sealed class LlmHealthCheck(
    ILlmConfigRepository repository,
    IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            string? configuredTenantId = configuration["Authentication:DevelopmentTenantId"];
            string tenantId = string.IsNullOrWhiteSpace(configuredTenantId)
                ? "development"
                : configuredTenantId;
            IReadOnlyList<LlmProviderProfile> profiles = await repository
                .ListAsync(tenantId, cancellationToken)
                .ConfigureAwait(false);
            int configuredCount = profiles.Count(IsConfigured);
            if (configuredCount == 0)
            {
                return HealthCheckResult.Degraded("No LLM provider is configured.");
            }

            return HealthCheckResult.Healthy(
                $"LLM provider configuration is available. Providers: {configuredCount}.",
                new Dictionary<string, object> { ["providerCount"] = configuredCount });
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "Failed to read LLM provider configuration.",
                exception);
        }
    }

    private static bool IsConfigured(LlmProviderProfile profile) =>
        !string.IsNullOrWhiteSpace(profile.Id)
        && !string.IsNullOrWhiteSpace(profile.Endpoint)
        && !string.IsNullOrWhiteSpace(profile.ModelId)
        && profile.ContextTokens > 0;
}
