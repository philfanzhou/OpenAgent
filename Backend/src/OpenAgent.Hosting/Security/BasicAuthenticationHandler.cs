using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Hosting.Authentication;

namespace OpenAgent.Hosting.Security;

/// <summary>
/// Temporary basic authentication boundary. Credentials are only decoded; the
/// username and password are intentionally not checked against a user store.
/// Authorization is a separate future concern.
/// </summary>
internal sealed class BasicAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AgentAuthenticationOptions> authenticationOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "Basic";

    private readonly AgentAuthenticationOptions _authenticationOptions = authenticationOptions.Value;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? authorization = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            if (IsDevelopmentAnonymousAllowed())
            {
                return Task.FromResult(Succeed(
                    _authenticationOptions.DevelopmentUserId,
                    _authenticationOptions.DevelopmentTenantId));
            }

            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string encoded = authorization["Basic ".Length..].Trim();
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid Basic credentials."));
        }

        int separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid Basic credentials."));
        }

        string username = decoded[..separator];
        return Task.FromResult(Succeed(username, _authenticationOptions.DevelopmentTenantId));
    }

    private AuthenticateResult Succeed(string username, string? tenantId)
    {
        string userId = string.IsNullOrWhiteSpace(username)
            ? _authenticationOptions.DevelopmentUserId
            : username;
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, userId),
            new("sub", userId),
            new("auth_mode", SchemeName)
        };

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            claims.Add(new Claim("tenant_id", tenantId));
            claims.Add(new Claim("tid", tenantId));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private bool IsDevelopmentAnonymousAllowed() =>
        _authenticationOptions.AllowDevelopmentAnonymous
        && (Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? "Production").Equals("Development", StringComparison.OrdinalIgnoreCase);
}
