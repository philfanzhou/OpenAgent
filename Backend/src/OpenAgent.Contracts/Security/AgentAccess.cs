using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Contracts.Security;

/// <summary>
/// Per-Agent allow-list rule shared by the Agent directory and the execution path.
/// Tenant isolation is enforced separately.
/// </summary>
public static class AgentAccess
{
    public static bool IsAllowed(AgentConfig config, IAgentUserContext user)
    {
        if (config.AllowedUserIds.Count == 0
            && config.AllowedGroups.Count == 0
            && config.AllowedRoles.Count == 0)
        {
            return true;
        }

        return config.AllowedUserIds.Contains(user.UserId, StringComparer.Ordinal)
            || config.AllowedGroups.Intersect(user.Groups, StringComparer.Ordinal).Any()
            || config.AllowedRoles.Intersect(user.Roles, StringComparer.Ordinal).Any();
    }
}
