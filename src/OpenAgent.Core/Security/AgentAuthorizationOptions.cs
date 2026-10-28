namespace OpenAgent.Core.Security;

public sealed class AgentAuthorizationOptions
{
    public AgentAuthorizationMode Mode { get; set; } = AgentAuthorizationMode.AllowAll;
}
