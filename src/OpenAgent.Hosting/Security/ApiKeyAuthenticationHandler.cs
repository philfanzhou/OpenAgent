using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Hosting.Security;

/// <summary>
/// Authenticates a Bearer API key by resolving it against the database-backed
/// identity store. The handler itself does not contain credential or tenant
/// configuration and never accepts tenant identity from request headers.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IThirdPartyApiKeyIdentityResolver identityResolver)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "ApiKey";
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? authorization = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string apiKey = authorization[BearerPrefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return AuthenticateResult.Fail("Bearer API key is required.");
        }

        ThirdPartyApiKeyIdentity? identity = await identityResolver
            .ResolveAsync(apiKey, Context.RequestAborted)
            .ConfigureAwait(false);
        if (identity == null)
        {
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, identity.UserId),
            new("sub", identity.UserId),
            new("tenant_id", identity.TenantId),
            new("tid", identity.TenantId),
            new("auth_mode", SchemeName)
        };
        if (!string.IsNullOrWhiteSpace(identity.Username))
        {
            claims.Add(new Claim("preferred_username", identity.Username));
        }
        if (!string.IsNullOrWhiteSpace(identity.Email))
        {
            claims.Add(new Claim("email", identity.Email));
        }
        claims.AddRange(identity.Roles.Select(role => new Claim("roles", role)));
        claims.AddRange(identity.Groups.Select(group => new Claim("groups", group)));
        claims.AddRange(identity.Audience.Select(audience => new Claim("aud", audience)));
        claims.AddRange(identity.Claims.Select(claim => new Claim(claim.Key, claim.Value)));

        var identityPrincipal = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(identityPrincipal),
            SchemeName));
    }
}
