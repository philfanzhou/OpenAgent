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
        if (userContext.Roles.Contains("Admin", StringComparer.OrdinalIgnoreCase))
        {
            return Task.FromResult(true);
        }

        string resource = request.ResourceType.ToString().ToLowerInvariant();
        string[] requiredScopes =
        [
            "agent.admin",
            $"{resource}.{request.Action.ToLowerInvariant()}",
            $"agent.{request.Action.ToLowerInvariant()}"
        ];
        HashSet<string> scopes = userContext.Claims
            .Where(item => item.Key is "scope" or "scp" or "permissions")
            .SelectMany(item => item.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(requiredScopes.Any(scopes.Contains));
    }
}
