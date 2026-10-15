namespace OpenAgent.Capabilities.Security;

public sealed class AgentAuthorizationOptions
{
    public AgentAuthorizationMode Mode { get; set; } = AgentAuthorizationMode.AllowAll;
}
