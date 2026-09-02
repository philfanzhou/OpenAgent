using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Hosting.Authentication;

namespace OpenAgent.Hosting.Security;

/// <summary>
/// Authenticates a configured third-party API key without contacting Keycloak.
/// The key maps to a server-owned subject and tenant claim; request headers
/// cannot choose either value.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<AgentAuthenticationOptions> authenticationOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "ApiKey";
    private const string ApiKeyHeader = "X-API-Key";
    private const string BearerPrefix = "Bearer ";

    private readonly AgentAuthenticationOptions _authenticationOptions = authenticationOptions.Value;

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? apiKey = Request.Headers[ApiKeyHeader].FirstOrDefault();
        string? authorization = Request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authorization)
            && authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            string bearerKey = authorization[BearerPrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(apiKey)
                && !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(apiKey),
                    Encoding.UTF8.GetBytes(bearerKey)))
            {
                return Task.FromResult(AuthenticateResult.Fail(
                    "X-API-Key and Authorization credentials do not match."));
            }

            apiKey = string.IsNullOrWhiteSpace(apiKey) ? bearerKey : apiKey;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!IsValidApiKey(apiKey))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, $"integration:{_authenticationOptions.ApiKeyClientId}"),
            new("sub", $"integration:{_authenticationOptions.ApiKeyClientId}"),
            new("client_id", _authenticationOptions.ApiKeyClientId),
            new("auth_mode", SchemeName),
            new("tenant_id", _authenticationOptions.ApiKeyTenantId!),
            new("tid", _authenticationOptions.ApiKeyTenantId!),
            new("aud", _authenticationOptions.ApiKeyAudience)
        };

        string scope = string.Join(
            ' ',
            _authenticationOptions.ApiKeyScopes
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(value => value.Split(
                    [' ', ','],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
        if (!string.IsNullOrWhiteSpace(scope))
        {
            claims.Add(new Claim("scope", scope));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    private bool IsValidApiKey(string apiKey)
    {
        byte[] actual = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(_authenticationOptions.ApiKeyHash!);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
