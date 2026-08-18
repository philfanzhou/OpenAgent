using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenAgent.Hosting.Authentication;

namespace OpenAgent.Hosting.Security;

/// <summary>
/// Development-only basic authentication boundary. Credentials are validated
/// against <see cref="DevelopmentCredentials"/> (admin/admin, test/test).
/// Authorization is a separate future concern.
/// </summary>
internal sealed class BasicAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AgentAuthenticationOptions> authenticationOptions,
    IHostEnvironment environment)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "Basic";

    private readonly AgentAuthenticationOptions _authenticationOptions = authenticationOptions.Value;
    private readonly IHostEnvironment _environment = environment;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!_environment.IsDevelopment())
        {
            return Task.FromResult(AuthenticateResult.Fail(
                "Basic authentication is available only in Development."));
        }

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
        string password = decoded[(separator + 1)..];

        if (!DevelopmentCredentials.IsValid(username, password))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid username or password."));
        }

        string tenantId = _authenticationOptions.AllowTenantHeader
            ? Request.Headers["X-Tenant-Id"].FirstOrDefault()
                ?? Request.Headers["X-TenantId"].FirstOrDefault()
                ?? _authenticationOptions.DevelopmentTenantId
            : _authenticationOptions.DevelopmentTenantId;

        return Task.FromResult(Succeed(username, tenantId));
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
        && _environment.IsDevelopment();
}
