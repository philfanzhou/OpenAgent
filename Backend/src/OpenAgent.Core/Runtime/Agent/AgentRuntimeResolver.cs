using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
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
