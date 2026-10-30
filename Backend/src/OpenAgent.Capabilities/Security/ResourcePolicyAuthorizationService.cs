using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Capabilities.Security;

internal sealed class ResourcePolicyAuthorizationService(
    IResourcePolicyStore store,
    IOptions<AgentAuthorizationOptions> options) : IAgentAuthorizationService
{
    private readonly ClaimsAgentAuthorizationService _claims = new();
    private readonly ConcurrentDictionary<AgentAuthorizationRequest, (IAgentUserContext User, ResourceAccessSnapshot Snapshot)> _grants = new();

    public async Task<bool> IsAuthorizedAsync(AgentAuthorizationRequest request, IAgentUserContext userContext,
        CancellationToken cancellationToken = default)
    {
        if (!userContext.IsAuthenticated || string.IsNullOrWhiteSpace(userContext.TenantId)
            || string.IsNullOrWhiteSpace(userContext.UserId)) return false;
        ResourceAccessSnapshot snapshot = await store.ReadAsync(userContext.TenantId, request, cancellationToken).ConfigureAwait(false);
        bool allowed = await EvaluateAsync(request, userContext, snapshot, cancellationToken).ConfigureAwait(false);
        if (allowed) _grants.TryAdd(request, (userContext, snapshot));
        return allowed;
    }

    internal async Task<bool> RevalidateAsync(CancellationToken ct)
    {
        foreach (var grant in _grants)
        {
            ResourceAccessSnapshot current = await store.ReadAsync(grant.Value.User.TenantId!, grant.Key, ct).ConfigureAwait(false);
            // Configuration rotation invalidates capabilities already handed to the harness, even
            // if its permission still evaluates to allow. A new turn can acquire the new revision.
            if (current.Revision != grant.Value.Snapshot.Revision
                || !await EvaluateAsync(grant.Key, grant.Value.User, current, ct).ConfigureAwait(false)) return false;
        }
        return true;
    }

    private async Task<bool> EvaluateAsync(AgentAuthorizationRequest request, IAgentUserContext user,
        ResourceAccessSnapshot snapshot, CancellationToken ct)
    {
        if (!snapshot.Exists) return false;
        if (snapshot.Policy is null)
            return !options.Value.RequireResourcePolicy && await _claims.IsAuthorizedAsync(request, user, ct).ConfigureAwait(false);
        ResourcePolicyRule[] matching = snapshot.Policy.Rules.Where(rule =>
            (rule.Actions.Contains("*", StringComparer.Ordinal) || rule.Actions.Contains(request.Action, StringComparer.OrdinalIgnoreCase))
            && (rule.UserIds.Length == 0 && rule.Roles.Length == 0
                || rule.UserIds.Contains(user.UserId, StringComparer.Ordinal)
                || rule.Roles.Intersect(user.Roles, StringComparer.OrdinalIgnoreCase).Any())).ToArray();
        return !matching.Any(rule => rule.Deny) && matching.Any(rule => !rule.Deny)
            && await _claims.IsAuthorizedAsync(request, user, ct).ConfigureAwait(false);
    }
}
