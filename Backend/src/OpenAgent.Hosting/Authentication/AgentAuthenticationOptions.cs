namespace OpenAgent.Hosting.Authentication;

public enum AgentAuthenticationMode
{
    Basic,
    JwtBearer,
    ApiKey
}

public sealed class AgentAuthenticationOptions
{
    public AgentAuthenticationMode Mode { get; set; } = AgentAuthenticationMode.Basic;
    public bool EnableKeycloak { get; set; } = true;
    public bool AllowDevelopmentAnonymous { get; set; } = true;
    public string DevelopmentUserId { get; set; } = "development-user";
    public string DevelopmentTenantId { get; set; } = "development";
    public string? Authority { get; set; }
    /// <summary>
    /// Optional internal OIDC discovery address. This is useful when the public
    /// issuer is reachable by the browser but service-to-service metadata must
    /// use a private Docker or cluster address.
    /// </summary>
    public string? MetadataAddress { get; set; }
    public string? Audience { get; set; }
    public string? ClientId { get; set; }
    /// <summary>
    /// SHA-256 hex digest of the API key used when <see cref="Mode"/> is
    /// <see cref="AgentAuthenticationMode.ApiKey"/>. Store this through a
    /// secret configuration source rather than committing the key.
    /// </summary>
    public string? ApiKeyHash { get; set; }
    public string? ApiKeyTenantId { get; set; }
    public string ApiKeyClientId { get; set; } = "third-party";
    public string[] ApiKeyScopes { get; set; } = [];
    public string ApiKeyAudience { get; set; } = "openagent-api";
    // Keep the bound value empty by default. The options binder appends array
    // values to initialized arrays, so setting defaults here would duplicate
    // configured scopes. The authentication endpoint supplies the defaults
    // when no scopes are configured.
    public string[] Scopes { get; set; } = [];
    public bool RequireHttpsMetadata { get; set; } = true;
    public int ClockSkewSeconds { get; set; } = 60;
}
