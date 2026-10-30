namespace OpenAgent.Capabilities.Security;

public sealed class AgentAuthorizationOptions
{
    /// <summary>Enable only after installing tenant resource policies; false retains legacy scope fallback.</summary>
    public bool RequireResourcePolicy { get; set; }

    public AgentAuthorizationMode Mode { get; set; } = AgentAuthorizationMode.AllowAll;
}
