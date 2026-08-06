using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Hosting.Authentication;

namespace OpenAgent.Hosting.Security;

internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AgentAuthenticationOptions> authenticationOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "ApiKey";
    private readonly AgentAuthenticationOptions _authenticationOptions = authenticationOptions.Value;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? apiKey = Request.Headers["X-API-Key"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(apiKey)) return Task.FromResult(AuthenticateResult.NoResult());
        if (!_authenticationOptions.ApiKeys.TryGetValue(apiKey, out ApiKeyIdentityOptions? identityOptions))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, identityOptions.UserId),
            new("sub", identityOptions.UserId)
        };
        if (!string.IsNullOrWhiteSpace(identityOptions.TenantId))
        {
            claims.Add(new Claim("tid", identityOptions.TenantId));
        }
        claims.AddRange(identityOptions.Roles.Select(role => new Claim("roles", role)));
        claims.AddRange(identityOptions.Groups.Select(group => new Claim("groups", group)));
        if (identityOptions.Scopes.Count > 0)
        {
            claims.Add(new Claim("scope", string.Join(' ', identityOptions.Scopes)));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
