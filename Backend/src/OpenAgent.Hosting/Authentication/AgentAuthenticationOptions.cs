namespace OpenAgent.Hosting.Authentication;

public enum AgentAuthenticationMode
{
    Basic,
    JwtBearer,
    Gateway
}

public sealed class AgentAuthenticationOptions
{
    public AgentAuthenticationMode Mode { get; set; } = AgentAuthenticationMode.Basic;
    public bool AllowDevelopmentAnonymous { get; set; } = true;
    public string DevelopmentUserId { get; set; } = "development-user";
    public string DevelopmentTenantId { get; set; } = "development";
    public string? Authority { get; set; }
    public string? Audience { get; set; }
    public string? ClientId { get; set; }
    public string[] Scopes { get; set; } = ["openid", "profile"];
    public bool RequireHttpsMetadata { get; set; } = true;
    public int ClockSkewSeconds { get; set; } = 60;
}
