using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace OpenAgent.Hosting.Authentication;

public sealed class AgentDelegationTokenService(
    IOptions<AgentDelegationTokenOptions> options) : IAgentDelegationTokenService
{
    private readonly AgentDelegationTokenOptions _options = options.Value;

    public string CreateToken(
        AgentDelegationIdentity identity)
    {
        SymmetricSecurityKey key = CreateSigningKey();
        DateTime issuedAt = DateTime.UtcNow;
        List<Claim> claims =
        [
            new(JwtRegisteredClaimNames.Sub, identity.UserId),
            new(AgentDelegationTokenClaims.AuthenticationMode,
                AgentDelegationTokenClaims.ProviderDelegation),
            new(AgentDelegationTokenClaims.Scope,
                AgentDelegationTokenClaims.ProviderScope)
        ];
        string? tenantId = identity.TenantId;
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            claims.Add(new Claim("tenant_id", tenantId));
        }

        claims.AddRange(identity.Roles.Select(role => new Claim("roles", role)));
        claims.AddRange(identity.Groups.Select(group => new Claim("groups", group)));
        foreach ((string type, string value) in identity.Claims)
        {
            if (IsReservedClaim(type))
            {
                continue;
            }

            claims.Add(new Claim(type, value));
        }

        claims.AddRange(identity.Audience.Select(audience => new Claim(
            AgentDelegationTokenClaims.UserAudience,
            audience)));
        JwtSecurityToken token = new(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: issuedAt,
            expires: issuedAt.AddSeconds(_options.LifetimeSeconds),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    internal TokenValidationParameters CreateValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidIssuer = _options.Issuer,
        ValidateAudience = true,
        ValidAudience = _options.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = CreateSigningKey(),
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(5),
        NameClaimType = JwtRegisteredClaimNames.Sub,
        RoleClaimType = "roles"
    };

    private SymmetricSecurityKey CreateSigningKey()
    {
        if (string.IsNullOrWhiteSpace(_options.SigningKey)
            || Encoding.UTF8.GetByteCount(_options.SigningKey) < 32)
        {
            throw new InvalidOperationException(
                "Authentication:ProviderToken:SigningKey must contain at least 32 UTF-8 bytes.");
        }

        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
    }

    private static bool IsReservedClaim(string type) => type switch
    {
        JwtRegisteredClaimNames.Sub
            or JwtRegisteredClaimNames.Aud
            or JwtRegisteredClaimNames.Exp
            or JwtRegisteredClaimNames.Iat
            or JwtRegisteredClaimNames.Iss
            or JwtRegisteredClaimNames.Jti
            or JwtRegisteredClaimNames.Nbf
            or AgentDelegationTokenClaims.AuthenticationMode
            or AgentDelegationTokenClaims.Scope
            or "tenant_id"
            or "roles"
            or "groups"
            or AgentDelegationTokenClaims.UserAudience => true,
        _ => false
    };
}
