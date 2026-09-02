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
    private readonly IAgentSecretResolver _secrets;

    public AgentRuntimeResolver(
        IAgentConfigProvider configs,
        AgentAuthorizationGate authorization,
        IAgentSecretResolver? secrets = null)
    {
        _configs = configs;
        _authorization = authorization;
        _secrets = secrets ?? new MissingAgentSecretResolver();
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

        LlmConfig authorizedModel = await _authorization.ResolveAuthorizedModelAsync(
                agentId,
                config.Llm,
                userContext,
                cancellationToken)
            .ConfigureAwait(false);
        LlmConfig model = CloneForExecution(authorizedModel);

        await ResolveApiKeyAsync(
                model,
                userContext.TenantId!,
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

    private static LlmConfig CloneForExecution(LlmConfig model) => new()
    {
        TenantId = model.TenantId,
        Provider = model.Provider,
        Format = model.Format,
        ModelId = model.ModelId,
        ApiKeySecretRef = model.ApiKeySecretRef,
        ApiKey = model.ApiKey,
        Endpoint = model.Endpoint,
        Temperature = model.Temperature
    };

    private async Task ResolveApiKeyAsync(
        LlmConfig model,
        string tenantId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(model.ApiKey)
            || string.IsNullOrWhiteSpace(model.ApiKeySecretRef))
        {
            return;
        }

        model.ApiKey = await _secrets.ResolveAsync(
                tenantId,
                model.ApiKeySecretRef,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"LLM secret reference '{model.ApiKeySecretRef}' is not configured for tenant '{tenantId}'.");
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
