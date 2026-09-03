using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Security;

internal sealed class AgentAuthorizationGate
{
    private readonly IAgentAuthorizationService _authorizationService;
    public AgentAuthorizationGate(IAgentAuthorizationService authorizationService)
    {
        _authorizationService = authorizationService;
    }

    internal Task EnsureAgentAuthorizedAsync(
        string agentId,
        IAgentUserContext userContext,
        CancellationToken cancellationToken) =>
        EnsureAuthorizedAsync(
            agentId,
            AgentResourceType.Agent,
            agentId,
            "execute",
            userContext,
            cancellationToken);

    internal Task EnsureModelAuthorizedAsync(
        string agentId,
        LlmConfig model,
        IAgentUserContext userContext,
        CancellationToken cancellationToken) =>
        EnsureAuthorizedAsync(
            agentId,
            AgentResourceType.Model,
            $"{model.Provider}/{model.ModelId}",
            "invoke",
            userContext,
            cancellationToken);

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
