using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;
using OpenAgent.Core.Abstract;

namespace OpenAgent.Core.Security;

internal sealed class AgentAuthorizationGate
{
    private readonly IAgentAuthorizationService _authorizationService;
    private readonly ILlmRegistry _models;

    public AgentAuthorizationGate(
        IAgentAuthorizationService authorizationService,
        ILlmRegistry models)
    {
        _authorizationService = authorizationService;
        _models = models;
    }

    internal Task<LlmConfig> ResolveAuthorizedModelAsync(
        string agentId,
        LlmConfig configuredModel,
        IAgentUserContext userContext,
        CancellationToken cancellationToken) =>
        ResolveAuthorizedModelAsync(
            agentId,
            configuredModel,
            requireCatalogEntry: false,
            userContext,
            cancellationToken);

    internal async Task<LlmConfig> ResolveAuthorizedModelAsync(
        string agentId,
        LlmConfig configuredModel,
        bool requireCatalogEntry,
        IAgentUserContext userContext,
        CancellationToken cancellationToken)
    {
        LlmConfig model = _models.ResolveConfig(configuredModel, requireCatalogEntry);
        if (!string.IsNullOrWhiteSpace(configuredModel.Provider)
            && !string.Equals(model.TenantId, userContext.TenantId, StringComparison.Ordinal))
        {
            throw new TenantDataIsolationException(
                userContext.TenantId,
                model.TenantId,
                "LLM profile does not belong to the authenticated tenant.");
        }
        string provider = string.IsNullOrWhiteSpace(model.Provider)
            ? model.Format.ToString()
            : model.Provider;
        await EnsureAuthorizedAsync(
            agentId,
            AgentResourceType.Model,
            $"{provider}/{model.ModelId}",
            "invoke",
            userContext,
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(model.Endpoint))
        {
            throw new AgentException(
                AgentErrorCode.DependencyUnavailable,
                $"LLM endpoint is empty after resolving config for agent '{agentId}'.");
        }

        if (string.IsNullOrWhiteSpace(model.ApiKey)
            || model.ApiKey.StartsWith("***", StringComparison.Ordinal))
        {
            throw new AgentException(
                AgentErrorCode.DependencyUnavailable,
                $"LLM credentials are not available for agent '{agentId}'.");
        }

        return model;
    }

    internal async Task EnsureAuthorizedAsync(
        string agentId,
        AgentResourceType resourceType,
        string resourceId,
        string action,
        IAgentUserContext userContext,
        CancellationToken cancellationToken)
    {
        AgentAuthorizationRequest request = new(agentId, resourceType, resourceId, action);
        bool isAuthorized = await _authorizationService.IsAuthorizedAsync(
            request,
            userContext,
            cancellationToken).ConfigureAwait(false);

        if (!isAuthorized)
        {
            throw new AgentException(
                AgentErrorCode.PermissionDenied,
                $"Access denied for {resourceType} resource '{resourceId}'");
        }
    }

    internal Task<bool> IsAuthorizedAsync(
        string agentId,
        AgentResourceType resourceType,
        string resourceId,
        string action,
        IAgentUserContext userContext,
        CancellationToken cancellationToken)
    {
        AgentAuthorizationRequest request = new(agentId, resourceType, resourceId, action);
        return _authorizationService.IsAuthorizedAsync(request, userContext, cancellationToken);
    }

    internal Task<bool> IsAvailableAsync(
        string agentId,
        AgentResourceType resourceType,
        string resourceId,
        IAgentUserContext userContext,
        CancellationToken cancellationToken)
    {
        return IsAuthorizedAsync(
            agentId,
            resourceType,
            resourceId,
            "use",
            userContext,
            cancellationToken);
    }
}
