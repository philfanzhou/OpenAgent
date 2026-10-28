using System.Text.Json.Serialization;

namespace OpenAgent.Contracts.Responses;

/// <summary>认证配置声明（GET /api/v1/auth/config），供前端决定登录界面形态。</summary>
public sealed class AuthConfigResponse
{
    /// <summary>认证模式名（JwtBearer / Basic / ApiKey）。</summary>
    public required string Mode { get; init; }

    public bool Development { get; init; }

    public required KeycloakConfigResponse Keycloak { get; init; }

    public required PasswordAuthConfigResponse Password { get; init; }

    public required AnonymousAuthConfigResponse Anonymous { get; init; }

    /// <summary>OIDC 端点信息；仅 JwtBearer 模式返回，其他模式为 null。</summary>
    public OidcConfigResponse? Oidc { get; init; }
}

public sealed class KeycloakConfigResponse
{
    public bool Enabled { get; init; }
}

public sealed class PasswordAuthConfigResponse
{
    public bool Enabled { get; init; }

    /// <summary>密码登录端点；仅开发环境 Basic 模式启用。</summary>
    public required string Endpoint { get; init; }
}

public sealed class AnonymousAuthConfigResponse
{
    public bool Enabled { get; init; }
}

public sealed class OidcConfigResponse
{
    public string? Authority { get; init; }

    public string? ClientId { get; init; }

    public string? Audience { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = ["openid", "profile"];
}

/// <summary>开发环境密码登录颁发的令牌（POST /api/v1/auth/password/token）。字段保持 snake_case 以兼容既有前端。</summary>
public sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("token_type")]
    public required string TokenType { get; init; }
}
