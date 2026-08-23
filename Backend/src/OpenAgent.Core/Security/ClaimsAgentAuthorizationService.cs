using OpenAgent.Authorization;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Security;

internal sealed class ClaimsAgentAuthorizationService : IAgentAuthorizationService
{
    public Task<bool> IsAuthorizedAsync(
        AgentAuthorizationRequest request,
        IAgentUserContext userContext,
        CancellationToken cancellationToken = default)
    {
        if (!userContext.IsAuthenticated) return Task.FromResult(false);
        string requiredPermission = ResolvePermission(request);
        bool allowed = PermissionMatcher.IsAllowed(
            PermissionMatcher.ReadPermissions(userContext.Claims),
            requiredPermission,
            request.ResourceId);
        return Task.FromResult(allowed);
    }

    private static string ResolvePermission(AgentAuthorizationRequest request) =>
        (request.ResourceType, request.Action.ToLowerInvariant()) switch
        {
            (AgentResourceType.Agent, "execute") => PermissionCatalog.AgentExecute,
            (AgentResourceType.Model, "invoke") => PermissionCatalog.ModelInvoke,
            (AgentResourceType.Tool, "use") => PermissionCatalog.ToolUse,
            // Core uses the generic availability action "use" while the public
            // authorization contract deliberately names function execution "invoke".
            (AgentResourceType.Function, "use" or "invoke") => PermissionCatalog.FunctionInvoke,
            (AgentResourceType.Mcp, "use") => PermissionCatalog.McpUse,
            (AgentResourceType.Skill, "use") => PermissionCatalog.SkillUse,
            _ => $"{request.ResourceType.ToString().ToLowerInvariant()}.{request.Action.ToLowerInvariant()}"
        };
}
