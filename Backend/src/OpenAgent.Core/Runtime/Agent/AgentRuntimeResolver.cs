using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Contracts.Requests;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Runtime.Agent;

/// <summary>
/// Resolves the Agent and independently selected LLM profile for one execution.
/// </summary>
internal sealed class AgentRuntimeResolver(
    IAgentConfigProvider configs,
    ILlmConfigProvider models,
    AgentAuthorizationGate authorization) : IAgentRuntimeResolver
{
    public async Task<AgentRuntimeProfile> ResolveAsync(
        string agentId,
        string llmProfileId,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("Agent id is required.", nameof(agentId));
        }
        if (string.IsNullOrWhiteSpace(llmProfileId))
        {
            throw new ArgumentException("LLM profile id is required.", nameof(llmProfileId));
        }

        string tenantId = userContext.TenantId
            ?? throw new TenantDataIsolationException(
                null,
                null,
                "TenantId is required but not provided");
        AgentConfig config = await configs.GetConfigAsync(
                agentId,
                tenantId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Agent configuration not found: {agentId}");
        ValidateSkillTenant(agentId, config, userContext);
        await authorization.EnsureAgentAuthorizedAsync(
                agentId,
                userContext,
                cancellationToken)
            .ConfigureAwait(false);

        LlmProviderProfile profile = await models.GetAsync(
                tenantId,
                llmProfileId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"LLM profile not found: {llmProfileId}");
        LlmConfig model = CreateExecutionModel(profile);
        await authorization.EnsureModelAuthorizedAsync(
                agentId,
                model,
                userContext,
                cancellationToken)
            .ConfigureAwait(false);

        Validate(agentId, config, model);
        return new AgentRuntimeProfile
        {
            AgentId = agentId,
            Config = config,
            Model = ModelTokenLimitResolver.ApplyDefaults(model, config),
        };
    }

    private static LlmConfig CreateExecutionModel(LlmProviderProfile profile) => new()
    {
        TenantId = profile.TenantId,
        Provider = profile.Id,
        Format = profile.Format,
        ModelId = profile.ModelId,
        ApiKey = profile.ApiKey,
        Endpoint = profile.Endpoint,
        Temperature = profile.Temperature,
        ContextTokens = profile.ContextTokens,
        MaxOutputTokens = profile.MaxOutputTokens,
        TokenCapabilities = new LlmTokenCapabilities
        {
            ContextWindowTokens = profile.ContextTokens,
            MaxOutputTokens = profile.MaxOutputTokens,
            SupportsMaxOutputTokens = profile.SupportsMaxOutputTokens
        },
        Modality = profile.Modality
    };

    private static void Validate(string agentId, AgentConfig config, LlmConfig model)
    {
        if (string.IsNullOrWhiteSpace(model.ModelId))
        {
            throw new InvalidOperationException($"LLM model id is empty for agent '{agentId}'.");
        }
        if (string.IsNullOrWhiteSpace(model.Endpoint))
        {
            throw new InvalidOperationException($"LLM endpoint is empty for agent '{agentId}'.");
        }
        if (model.ContextTokens <= 0)
        {
            throw new AgentException(AgentErrorCode.ConfigurationError, "LLM context window must be positive.");
        }
        if (config.MaxTurns < 0)
        {
            throw new InvalidOperationException($"MaxTurns cannot be negative for agent '{agentId}'.");
        }

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

}
