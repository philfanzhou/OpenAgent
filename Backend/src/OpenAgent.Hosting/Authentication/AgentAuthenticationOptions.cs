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
    public AuthenticationLoginOptions Login { get; set; } = new();
}

public sealed class AuthenticationLoginOptions
{
    public PasswordLoginOptions Password { get; set; } = new();
    public MicrosoftLoginOptions Microsoft { get; set; } = new();
}

public sealed class PasswordLoginOptions
{
    public bool Enabled { get; set; }
    public string TokenEndpoint { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Scope { get; set; } = "openid profile email";
}

public sealed class MicrosoftLoginOptions
{
    public bool Enabled { get; set; }
    public string Authority { get; set; } = string.Empty;
    public string AuthorizationEndpoint { get; set; } = string.Empty;
    public string TokenEndpoint { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];
}

public sealed class ApiKeyIdentityOptions
{
    public string UserId { get; set; } = string.Empty;
    public string? TenantId { get; set; }
    public List<string> Roles { get; set; } = new();
    public List<string> Groups { get; set; } = new();
    public List<string> Scopes { get; set; } = new();
}
