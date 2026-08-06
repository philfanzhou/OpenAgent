namespace OpenAgent.Core.Security;

public enum AgentAuthorizationMode
{
    AllowAll,
    Claims
}

public sealed class AgentAuthorizationOptions
{
    public AgentAuthorizationMode Mode { get; set; } = AgentAuthorizationMode.AllowAll;
}
