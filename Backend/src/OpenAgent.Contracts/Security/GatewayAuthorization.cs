namespace OpenAgent.Contracts.Security;

public static class GatewayClaimTypes
{
    public const string Permission = "openagent_permission";
}

public static class GatewayPermissions
{
    public const string AgentRead = "agent.read";
    public const string AgentExecute = "agent.execute";
    public const string AgentConfigRead = "agent.config.read";
    public const string AgentConfigWrite = "agent.config.write";
    public const string ModelInvoke = "model.invoke";
    public const string ToolUse = "tool.use";
    public const string FunctionInvoke = "function.invoke";
    public const string McpUse = "mcp.use";
    public const string SkillUse = "skill.use";
    public const string CapabilityTest = "capability.test";
    public const string ConversationRead = "conversation.read";
    public const string ConversationDelete = "conversation.delete";
    public const string IdentityRead = "identity.read";

    public static IReadOnlyList<string> All { get; } =
    [
        AgentRead,
        AgentExecute,
        AgentConfigRead,
        AgentConfigWrite,
        ModelInvoke,
        ToolUse,
        FunctionInvoke,
        McpUse,
        SkillUse,
        CapabilityTest,
        ConversationRead,
        ConversationDelete,
        IdentityRead
    ];
}

public static class GatewayPermissionMatcher
{
    public static bool IsAllowed(
        IEnumerable<string> grantedPermissions,
        string requiredPermission,
        string? resourceId = null)
    {
        if (string.IsNullOrWhiteSpace(requiredPermission))
        {
            return false;
        }

        HashSet<string> permissions = grantedPermissions
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Select(permission => permission.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (permissions.Contains("*") || permissions.Contains(requiredPermission))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(resourceId))
        {
            return false;
        }

        return permissions.Contains($"{requiredPermission}:*")
            || permissions.Contains($"{requiredPermission}:{resourceId}");
    }

    public static IEnumerable<string> ReadPermissions(
        IReadOnlyDictionary<string, string> claims)
    {
        return claims
            .Where(claim => claim.Key.Equals(GatewayClaimTypes.Permission, StringComparison.OrdinalIgnoreCase)
                || claim.Key.Equals("permissions", StringComparison.OrdinalIgnoreCase)
                || claim.Key.Equals("scope", StringComparison.OrdinalIgnoreCase)
                || claim.Key.Equals("scp", StringComparison.OrdinalIgnoreCase))
            .SelectMany(claim => claim.Value.Split(
                [' ', ','],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
