using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using OpenAgent.Capabilities.Security;

namespace OpenAgent.Application.Execution;

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

        string tenantId = userContext.TenantId
            ?? throw new TenantDataIsolationException(
                null,
                null,
                "TenantId is required but not provided");
        await authorization.EnsureAgentAuthorizedAsync(
                agentId,
                userContext,
                cancellationToken)
            .ConfigureAwait(false);
        AgentConfig config = await configs.GetConfigAsync(
                agentId,
                tenantId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Agent configuration not found: {agentId}");
        ValidateTenant(agentId, config, userContext);

        // 模型解析顺序：请求显式选择的 llmProfileId 优先；未携带时回落到
        // Agent 的 OpenCode 内嵌模型；两者都没有才视为配置缺失。
        await authorization.EnsureAuthorizedAsync(agentId, AgentResourceType.Model,
            string.IsNullOrWhiteSpace(llmProfileId) ? "@agent/" + agentId : llmProfileId,
            "invoke", userContext, cancellationToken).ConfigureAwait(false);
        LlmConfig model;
        if (string.IsNullOrWhiteSpace(llmProfileId))
        {
            OpenCodeModelConfig? inline = config.OpenCode?.Model;
            model = inline is null
                ? throw new InvalidOperationException(
                    $"Request selected no LLM profile and agent '{agentId}' has no OpenCode model configured.")
                : CreateInlineModel(config.TenantId, agentId, inline);
        }
        else
        {
            LlmProviderProfile profile = await models.GetAsync(
                    tenantId,
                    llmProfileId,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"LLM profile not found: {llmProfileId}");
            model = CreateExecutionModel(profile);
        }
        if (!string.Equals(model.TenantId, tenantId, StringComparison.Ordinal))
            throw new TenantDataIsolationException(tenantId, model.TenantId);
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
            Model = model,
        };
    }

    private static LlmConfig CreateInlineModel(string tenantId, string agentId, OpenCodeModelConfig model) => new()
    {
        TenantId = tenantId,
        Provider = "@agent/" + agentId,
        Format = model.Format,
        ModelId = model.ModelId,
        ApiKey = model.ApiKey,
        Endpoint = model.Endpoint,
        Temperature = model.Temperature,
        // 未配置上下文窗口时沿用驱动侧记录的回退值，避免必填项校验把内嵌配置挡下。
        ContextTokens = model.ContextTokens > 0 ? model.ContextTokens : 128000
    };

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
            throw new InvalidOperationException($"LLM context window must be greater than zero for agent '{agentId}'.");
        }
        if (config.MaxTurns < 0)
        {
            throw new InvalidOperationException($"MaxTurns cannot be negative for agent '{agentId}'.");
        }

    }

    private static void ValidateTenant(
        string agentId,
        AgentConfig config,
        IAgentUserContext userContext)
    {
        if (!string.Equals(config.TenantId, userContext.TenantId, StringComparison.Ordinal))
        {
            throw new TenantDataIsolationException(
                userContext.TenantId,
                config.TenantId,
                $"Agent '{agentId}' belongs to another tenant.");
        }
    }

}
