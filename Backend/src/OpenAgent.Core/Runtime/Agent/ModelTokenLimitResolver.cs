using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Runtime.Agent;

internal static class ModelTokenLimitResolver
{
    internal static AgentRuntimeProfile Apply(
        AgentRuntimeProfile profile,
        AgentRequest request)
    {
        EnsurePositive("context window", request.ContextWindowTokens);
        EnsurePositive("maximum output", request.MaxOutputTokens);

        LlmConfig configured = profile.Model;
        LlmTokenCapabilities capabilities = configured.TokenCapabilities;
        EnsureWithinCapability(
            "context window",
            request.ContextWindowTokens,
            capabilities.ContextWindowTokens);
        EnsureWithinCapability(
            "maximum output",
            request.MaxOutputTokens,
            capabilities.MaxOutputTokens);
        if (request.MaxOutputTokens.HasValue
            && !capabilities.SupportsMaxOutputTokens)
        {
            throw InvalidRequest(
                "The selected provider does not support the max output tokens parameter requested for this invocation.");
        }

        int? contextWindowTokens = request.ContextWindowTokens ?? configured.ContextWindowTokens;
        int? maxOutputTokens = request.MaxOutputTokens ?? configured.MaxOutputTokens;
        if (contextWindowTokens.HasValue
            && maxOutputTokens.HasValue
            && maxOutputTokens.Value >= contextWindowTokens.Value)
        {
            throw InvalidRequest(
                $"Effective maximum output tokens {maxOutputTokens} must be less than the effective context window {contextWindowTokens}.");
        }

        return new AgentRuntimeProfile
        {
            AgentId = profile.AgentId,
            Config = profile.Config,
            Model = new LlmConfig
            {
                TenantId = configured.TenantId,
                Provider = configured.Provider,
                Format = configured.Format,
                ModelId = configured.ModelId,
                ApiKey = configured.ApiKey,
                Endpoint = configured.Endpoint,
                Temperature = configured.Temperature,
                ContextWindowTokens = contextWindowTokens,
                MaxOutputTokens = maxOutputTokens,
                TokenCapabilities = capabilities
            }
        };
    }

    private static void EnsurePositive(string name, int? value)
    {
        if (value.HasValue && value.Value <= 0)
        {
            throw InvalidRequest($"The requested {name} token value must be positive.");
        }
    }

    private static void EnsureWithinCapability(
        string name,
        int? requested,
        int? capability)
    {
        if (requested.HasValue
            && capability.HasValue
            && requested.Value > capability.Value)
        {
            throw InvalidRequest(
                $"Requested {name} {requested} exceeds model capability {capability}.");
        }
    }

    private static AgentException InvalidRequest(string message) =>
        new(AgentErrorCode.InvalidRequest, message);
}
