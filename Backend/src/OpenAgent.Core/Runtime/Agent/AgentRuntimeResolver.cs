using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// Resolves the effective runtime profile before an Agent is constructed.
/// </summary>
internal sealed class AgentRuntimeResolver : IAgentRuntimeResolver
{
    private readonly IAgentConfigProvider _configs;
    private readonly AgentAuthorizationGate _authorization;

    public AgentRuntimeResolver(
        IAgentConfigProvider configs,
        AgentAuthorizationGate authorization)
    {
        _configs = configs;
        _authorization = authorization;
    }

    public async Task<AgentRuntimeProfile> ResolveAsync(
        string agentId,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("Agent id is required.", nameof(agentId));
        }

        AgentConfig config = await _configs.GetConfigAsync(
                agentId,
                userContext.TenantId
                    ?? throw new TenantDataIsolationException(
                        null,
                        null,
                        "TenantId is required but not provided"),
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Agent configuration not found: {agentId}");

        ValidateSkillTenant(agentId, config, userContext);

        LlmConfig model = await _authorization.ResolveAuthorizedModelAsync(
                agentId,
                config.Llm,
                userContext,
                cancellationToken)
            .ConfigureAwait(false);

        Validate(agentId, config, model);
        return new AgentRuntimeProfile
        {
            AgentId = agentId,
            Config = config,
            Model = model
        };
    }

    private static void Validate(string agentId, AgentConfig config, LlmConfig model)
    {
        if (string.IsNullOrWhiteSpace(model.ModelId))
        {
            throw new InvalidOperationException(
                $"LLM model id is empty for agent '{agentId}'.");
        }

        if (config.MaxTurns < 0)
        {
            throw new InvalidOperationException(
                $"MaxTurns cannot be negative for agent '{agentId}'.");
        }

        ValidateTokenLimits(agentId, config.Llm, model);
        ValidateContextPolicy(agentId, config.ContextPolicy);
    }

    private static void ValidateTokenLimits(
        string agentId,
        LlmConfig configuredModel,
        LlmConfig model)
    {
        LlmTokenCapabilities capabilities = model.TokenCapabilities;
        EnsurePositive(agentId, "model context window", capabilities.ContextWindowTokens);
        EnsurePositive(agentId, "model maximum output", capabilities.MaxOutputTokens);
        EnsurePositive(agentId, "Agent context window", configuredModel.ContextWindowTokens);
        EnsurePositive(agentId, "Agent maximum output", configuredModel.MaxOutputTokens);

        if (configuredModel.ContextWindowTokens.HasValue
            && capabilities.ContextWindowTokens.HasValue
            && configuredModel.ContextWindowTokens.Value > capabilities.ContextWindowTokens.Value)
        {
            throw ConfigurationError(
                agentId,
                $"Agent context window {configuredModel.ContextWindowTokens} exceeds model capability {capabilities.ContextWindowTokens}.");
        }
        if (configuredModel.MaxOutputTokens.HasValue
            && capabilities.MaxOutputTokens.HasValue
            && configuredModel.MaxOutputTokens.Value > capabilities.MaxOutputTokens.Value)
        {
            throw ConfigurationError(
                agentId,
                $"Agent maximum output {configuredModel.MaxOutputTokens} exceeds model capability {capabilities.MaxOutputTokens}.");
        }
        if (configuredModel.MaxOutputTokens.HasValue
            && !capabilities.SupportsMaxOutputTokens)
        {
            throw ConfigurationError(
                agentId,
                "The selected provider does not support the max output tokens parameter configured by the Agent.");
        }

        EnsureOutputFitsContext(
            agentId,
            capabilities.ContextWindowTokens,
            capabilities.MaxOutputTokens,
            "Model");
        EnsureOutputFitsContext(
            agentId,
            model.ContextWindowTokens,
            model.MaxOutputTokens,
            "Effective Agent");
    }

    private static void EnsurePositive(string agentId, string name, int? value)
    {
        if (value.HasValue && value.Value <= 0)
        {
            throw ConfigurationError(agentId, $"The {name} token value must be positive.");
        }
    }

    private static void EnsureOutputFitsContext(
        string agentId,
        int? contextWindowTokens,
        int? maxOutputTokens,
        string source)
    {
        if (contextWindowTokens.HasValue
            && maxOutputTokens.HasValue
            && maxOutputTokens.Value >= contextWindowTokens.Value)
        {
            throw ConfigurationError(
                agentId,
                $"{source} maximum output tokens must be less than its context window.");
        }
    }

    private static AgentException ConfigurationError(string agentId, string message) =>
        new(AgentErrorCode.ConfigurationError, $"Invalid LLM token configuration for agent '{agentId}': {message}");

    private static void ValidateSkillTenant(
        string agentId,
        AgentConfig config,
        IAgentUserContext userContext)
    {
        bool hasSkillBinding = config.Skills.EnabledSkills.Count > 0
            || config.Skills.Instances.Count > 0;
        // 调试用：空租户（存量）agent 的技能绑定不参与跨租户校验。
        if (hasSkillBinding
            && !string.IsNullOrWhiteSpace(config.TenantId)
            && !string.Equals(config.TenantId, userContext.TenantId, StringComparison.Ordinal))
        {
            throw new TenantDataIsolationException(
                userContext.TenantId,
                config.TenantId,
                $"Agent '{agentId}' cannot use Skills from another tenant.");
        }
    }

    private static void ValidateContextPolicy(string agentId, ContextPolicy? policy)
    {
        if (policy == null)
        {
            return;
        }

        if (policy.MaxTokens < 0 || policy.PreserveRecentTurns < 0)
        {
            throw new InvalidOperationException(
                $"ContextPolicy limits cannot be negative for agent '{agentId}'.");
        }

        if (policy.SummarizeOptions?.MaxSummaryTokens < 1)
        {
            throw new InvalidOperationException(
                $"ContextPolicy summary token limit must be positive for agent '{agentId}'.");
        }
    }
}
