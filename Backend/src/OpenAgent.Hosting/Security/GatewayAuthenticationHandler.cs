using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Authorization;
using OpenAgent.Hosting.Authorization;

namespace OpenAgent.Hosting.Security;

internal sealed class GatewayAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<GatewayAuthorizationOptions> gatewayOptions,
    GatewayGrantCodec codec)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "OpenAgentGateway";
    private readonly GatewayAuthorizationOptions _gatewayOptions = gatewayOptions.Value;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? token = Request.Headers[DelegatedPermissionHeaders.Grant].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!codec.TryDecode(token, _gatewayOptions.Audience, out GatewayGrantPayload? payload)
            || payload == null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired gateway grant."));
        }

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, payload.Subject),
            new(ClaimTypes.Name, payload.Subject),
            new("sub", payload.Subject),
            new("aud", payload.Audience),
            new("auth_mode", SchemeName),
            new("gateway_token_id", payload.TokenId)
        ];
        if (!string.IsNullOrWhiteSpace(payload.TenantId))
        {
            claims.Add(new Claim("tenant_id", payload.TenantId));
            claims.Add(new Claim("tid", payload.TenantId));
        }

        claims.AddRange(payload.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(payload.Groups.Select(group => new Claim("group", group)));
        claims.AddRange(payload.Permissions.Select(permission =>
            new Claim(PermissionClaimTypes.Permission, permission)));

        ClaimsIdentity identity = new(claims, SchemeName);
        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket = new(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
