using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Hosting.Authentication;

namespace OpenAgent.Hosting.Security;

// <summary>
// ⚠️ SECURITY NOTICE: This is a PASS-THROUGH authentication handler for development only.
// It accepts any request without credential validation and MUST be replaced with a
// real authentication handler (e.g., JwtBearer) before production use.
// See: https://github.com/your-org/OpenAgent/blob/main/docs/authentication.md
// </summary>
internal sealed class PassThroughAuthenticationHandler
    : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "PassThrough";

    public PassThroughAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<AgentAuthenticationOptions> authenticationOptions)
        : base(options, logger, encoder)
    {
        _authenticationOptions = authenticationOptions.Value;
    }

    private readonly AgentAuthenticationOptions _authenticationOptions;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? userId = Request.Headers["X-User-Id"].FirstOrDefault() ?? _authenticationOptions.DevelopmentUserId;
        string? tenantId = Request.Headers["X-Tenant-Id"].FirstOrDefault()
            ?? Request.Headers["X-TenantId"].FirstOrDefault()
            ?? _authenticationOptions.DevelopmentTenantId;

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, userId),
            new("sub", userId),
        };

        if (!string.IsNullOrEmpty(tenantId))
        {
            claims.Add(new Claim("tenant_id", tenantId));
            claims.Add(new Claim("tid", tenantId));
        }

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
