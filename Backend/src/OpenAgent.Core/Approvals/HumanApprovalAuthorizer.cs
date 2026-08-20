using OpenAgent.Contracts.Requests;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Core.Approvals;

internal static class HumanApprovalAuthorizer
{
    internal const string Permission = "approval.decide";

    internal static void EnsureCanDecide(IAgentUserContext user)
    {
        if (!CanDecide(user))
        {
            throw new AgentException(
                AgentErrorCode.PermissionDenied,
                "The authenticated user is not authorized to decide approvals");
        }
    }

    internal static bool CanDecide(IAgentUserContext user)
    {
        if (!user.IsAuthenticated)
        {
            return false;
        }
        if (user.Roles.Any(role =>
            string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
            || string.Equals(role, "ApprovalApprover", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return user.Claims
            .Where(claim =>
                string.Equals(claim.Key, "scope", StringComparison.OrdinalIgnoreCase)
                || string.Equals(claim.Key, "scp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(claim.Key, "permissions", StringComparison.OrdinalIgnoreCase))
            .SelectMany(claim => claim.Value.Split(
                [' ', ','],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Contains(Permission, StringComparer.OrdinalIgnoreCase);
    }
}
