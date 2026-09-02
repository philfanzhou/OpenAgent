namespace OpenAgent.Infrastructure.Entities;

internal sealed class ThirdPartyApiKeyEntity
{
    public required string ApiKeyId { get; init; }
    public required string Name { get; init; }
    public required string KeyHash { get; init; }
    public required string UserId { get; init; }
    public required string TenantId { get; init; }
    public string? Username { get; init; }
    public string? Email { get; init; }
    public string Scopes { get; init; } = string.Empty;
    public string Roles { get; init; } = string.Empty;
    public string Groups { get; init; } = string.Empty;
    public bool IsEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
}
