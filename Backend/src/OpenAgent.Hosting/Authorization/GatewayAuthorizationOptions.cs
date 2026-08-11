namespace OpenAgent.Hosting.Authorization;

public sealed class GatewayAuthorizationOptions
{
    public const string SectionName = "GatewayAuthorization";

    public string Issuer { get; set; } = "openagent-router";
    public string Audience { get; set; } = "openagent-engine";
    public string SigningKey { get; set; } = string.Empty;
    public Dictionary<string, string> AudienceSigningKeys { get; set; } =
        new(StringComparer.Ordinal);
    public int GrantLifetimeSeconds { get; set; } = 60;
    public int ClockSkewSeconds { get; set; } = 10;
    public int MaxGrantCharacters { get; set; } = 16_384;
    public List<string> AuthenticatedPermissions { get; set; } = [];
    public Dictionary<string, List<string>> RolePermissions { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}
