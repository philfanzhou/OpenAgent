using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Security;
using OpenAgent.Hosting.Authentication;

namespace OpenAgent.Hosting.Security;

/// <summary>
/// Validates configured Basic credentials. Built-in accounts are only available
/// in Development when no password has been configured.
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
        string? authorization = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            if (IsDevelopmentAnonymousAllowed())
            {
                return Task.FromResult(SucceedDevelopment(
                    _authenticationOptions.DevelopmentUserId));
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

        if (!BasicCredentials.IsValid(username, password, _authenticationOptions, _environment.IsDevelopment()))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid username or password."));
        }

        return Task.FromResult(string.IsNullOrWhiteSpace(_authenticationOptions.BasicPassword)
            ? SucceedDevelopment(username)
            : Succeed(username, _authenticationOptions.BasicTenantId));
    }

    private AuthenticateResult SucceedDevelopment(string username)
    {
        try
        {
            return Succeed(username, _authenticationOptions.DevelopmentTenantId);
        }
        catch (AgentException exception)
        {
            return AuthenticateResult.Fail(exception.Message);
        }
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
