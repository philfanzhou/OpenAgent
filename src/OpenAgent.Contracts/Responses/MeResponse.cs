namespace OpenAgent.Contracts.Responses;

/// <summary>当前认证用户信息（GET /api/v1/agent/me）。</summary>
public sealed class MeResponse
{
    public required string UserId { get; init; }

    public string? Username { get; init; }

    public string? Email { get; init; }

    public string? TenantId { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = [];

    public IReadOnlyList<string> Groups { get; init; } = [];

    public IReadOnlyDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();

    public IReadOnlyList<string> Audience { get; init; } = [];

    public bool IsAuthenticated { get; init; }
}
