namespace OpenAgent.Hosting.Authorization;

public static class GatewayAuthorizationDefaults
{
    public const string GrantHeaderName = "X-OpenAgent-Gateway-Grant";
    public const string PermissionClaimType = "openagent_permission";
    internal const string AgentExecutePermission = "agent.execute";
}
