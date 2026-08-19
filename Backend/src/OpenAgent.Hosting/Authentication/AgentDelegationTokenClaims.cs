namespace OpenAgent.Hosting.Authentication;

public static class AgentDelegationTokenClaims
{
    public const string AuthenticationMode = "auth_mode";
    public const string ProviderDelegation = "ProviderDelegation";
    public const string UserAudience = "user_audience";
    public const string Scope = "scope";
    public const string ProviderScope = "agent.provider";
}
