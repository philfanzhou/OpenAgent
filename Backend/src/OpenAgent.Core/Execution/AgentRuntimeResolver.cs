using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Conversation;
using OpenAgent.Contracts.Security;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Requests;
using OpenAgent.Core.Security;

namespace OpenAgent.Core.Execution;

/// <summary>
/// Resolves an authorized Agent and, for platform-managed models, its selected LLM profile.
/// </summary>
internal sealed class AgentRuntimeResolver(
    IAgentConfigProvider configs,
    ILlmConfigProvider models,
    AgentAuthorizationGate authorization, IOptions<AcpOptions>? acp = null) : IAgentRuntimeResolver
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

        AcpHarness? harness = config.Runtime.Adapter == "acp" && acp?.Value.Enabled == true
            ? acp.Value.Harnesses.Find(item => item.Id == config.Runtime.Harness) : null;
        if (harness != null && !harness.OpenCode && !harness.UsePlatformModel)
        {
            if (config.MaxTurns < 0)
                throw new InvalidOperationException($"MaxTurns cannot be negative for agent '{agentId}'.");
            // No platform model or credential is resolved for a harness-owned connection.
            return new AgentRuntimeProfile { AgentId = agentId, Config = config,
                Model = new LlmConfig { Provider = harness.Id, ModelId = "harness-default" } };
        }
        if (string.IsNullOrWhiteSpace(llmProfileId))
            throw new AgentException(AgentErrorCode.MissingRequiredField, "LlmProfileId is required for this runtime.");

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
            Model = model,
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
