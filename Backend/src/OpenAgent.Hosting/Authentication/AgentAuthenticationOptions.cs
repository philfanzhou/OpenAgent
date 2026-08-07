namespace OpenAgent.Hosting.Authentication;

public enum AgentAuthenticationMode
{
    Basic
}

public sealed class AgentAuthenticationOptions
{
    public AgentAuthenticationMode Mode { get; set; } = AgentAuthenticationMode.Basic;
    public bool AllowTenantHeader { get; set; } = true;
    public bool AllowDevelopmentAnonymous { get; set; } = true;
    public string DevelopmentUserId { get; set; } = "development-user";
    public string DevelopmentTenantId { get; set; } = "development";
}
