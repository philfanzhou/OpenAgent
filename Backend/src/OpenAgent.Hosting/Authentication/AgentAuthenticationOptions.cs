namespace OpenAgent.Hosting.Authentication;

public enum AgentAuthenticationMode
{
    PassThrough,
    JwtBearer,
    OpaqueIntrospection,
    ApiKey
}

public sealed class AgentAuthenticationOptions
{
    public AgentAuthenticationMode Mode { get; set; } = AgentAuthenticationMode.PassThrough;
    public string Authority { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public bool RequireHttpsMetadata { get; set; } = true;
    public string IntrospectionEndpoint { get; set; } = string.Empty;
    public string IntrospectionClientId { get; set; } = string.Empty;
    public string IntrospectionClientSecret { get; set; } = string.Empty;
    public bool AllowTenantHeader { get; set; }
    public Dictionary<string, ApiKeyIdentityOptions> ApiKeys { get; set; } = new();
}

public sealed class ApiKeyIdentityOptions
{
    public string UserId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public List<string> Roles { get; set; } = new();
    public List<string> Groups { get; set; } = new();
    public List<string> Scopes { get; set; } = new();
}
