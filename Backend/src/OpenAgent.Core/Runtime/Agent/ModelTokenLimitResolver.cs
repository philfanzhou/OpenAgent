using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Configuration;

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

        int contextWindowTokens = request.ContextWindowTokens ?? configured.ContextTokens;
        int? maxOutputTokens = request.MaxOutputTokens ?? configured.MaxOutputTokens;
        if (maxOutputTokens.HasValue
            && maxOutputTokens.Value >= contextWindowTokens)
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
                ContextTokens = contextWindowTokens,
                Modality = configured.Modality,
                MaxOutputTokens = maxOutputTokens,
                TokenCapabilities = capabilities
            }
        };
    }

    internal static LlmConfig ApplyDefaults(LlmConfig model, AgentConfig config)
    {
        TokenLimitValidator.ValidateConfiguration(model.ContextTokens, model.MaxOutputTokens);
        TokenLimitValidator.ValidateConfiguration(config.ContextWindowTokens, config.MaxOutputTokens);
        if (config.ContextWindowTokens > model.ContextTokens
            || (model.MaxOutputTokens.HasValue && config.MaxOutputTokens > model.MaxOutputTokens)
            || (config.MaxOutputTokens.HasValue && !model.TokenCapabilities.SupportsMaxOutputTokens))
        {
            throw new AgentException(AgentErrorCode.ConfigurationError,
                "Agent token defaults exceed the selected model capability or request an unsupported output parameter.");
        }
        model.ContextTokens = config.ContextWindowTokens ?? model.ContextTokens;
        model.MaxOutputTokens = config.MaxOutputTokens ?? model.MaxOutputTokens;
        TokenLimitValidator.ValidateConfiguration(model.ContextTokens, model.MaxOutputTokens);
        return model;
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
