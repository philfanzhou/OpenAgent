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
        CancellationToken cancellationToken = default) =>
        await ResolveAsync(agentId, userContext, null, cancellationToken).ConfigureAwait(false);

    public async Task<AgentRuntimeProfile> ResolveAsync(
        string agentId,
        IAgentUserContext userContext,
        LlmModelSelection? modelOverride,
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

        await _authorization.EnsureAuthorizedAsync(
            agentId,
            AgentResourceType.Agent,
            agentId,
            "execute",
            userContext,
            cancellationToken).ConfigureAwait(false);

        LlmConfig configuredModel = CreateConfiguredModel(config.Llm, modelOverride);

        LlmConfig model = await _authorization.ResolveAuthorizedModelAsync(
                agentId,
                configuredModel,
                modelOverride != null,
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

    private static LlmConfig CreateConfiguredModel(
        LlmConfig agentDefault,
        LlmModelSelection? modelOverride)
    {
        if (modelOverride == null)
        {
            return agentDefault;
        }

        if (string.IsNullOrWhiteSpace(modelOverride.Provider)
            || string.IsNullOrWhiteSpace(modelOverride.ModelId))
        {
            throw new AgentException(
                AgentErrorCode.MissingRequiredField,
                "A model override requires both provider and modelId.");
        }

        return new LlmConfig
        {
            Provider = modelOverride.Provider,
            ModelId = modelOverride.ModelId
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

        ValidateContextPolicy(agentId, config.ContextPolicy);
    }

    private static void ValidateSkillTenant(
        string agentId,
        AgentConfig config,
        IAgentUserContext userContext)
    {
        bool hasSkillBinding = config.Skills.EnabledSkills.Count > 0
            || config.Skills.Instances.Count > 0;
        if (hasSkillBinding
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

        if (string.IsNullOrWhiteSpace(policy.Strategy))
        {
            throw new InvalidOperationException(
                $"ContextPolicy strategy is required for agent '{agentId}'.");
        }

        string strategy = policy.Strategy.ToLowerInvariant();
        if (strategy is not ("summarize" or "sliding_window" or "none"))
        {
            throw new InvalidOperationException(
                $"Unsupported ContextPolicy strategy '{policy.Strategy}' for agent '{agentId}'.");
        }

        if (policy.MaxTokens < 0 || policy.PreserveRecentTurns < 0)
        {
            throw new InvalidOperationException(
                $"ContextPolicy limits cannot be negative for agent '{agentId}'.");
        }
    }
}
