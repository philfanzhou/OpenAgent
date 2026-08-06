using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAgent.Hosting.Authentication;

namespace OpenAgent.Hosting.Security;

internal sealed class OpaqueIntrospectionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHttpClientFactory httpClientFactory,
    IOptions<AgentAuthenticationOptions> authenticationOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "Introspection";
    internal const string ClientName = "AgentIntrospection";
    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly AgentAuthenticationOptions _authenticationOptions = authenticationOptions.Value;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? authorization = Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(authorization)
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        if (string.IsNullOrWhiteSpace(_authenticationOptions.IntrospectionEndpoint))
        {
            return AuthenticateResult.Fail("Introspection endpoint is not configured.");
        }

        string token = authorization["Bearer ".Length..].Trim();
        using var request = new HttpRequestMessage(HttpMethod.Post, _authenticationOptions.IntrospectionEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token })
        };
        if (!string.IsNullOrWhiteSpace(_authenticationOptions.IntrospectionClientId))
        {
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"{_authenticationOptions.IntrospectionClientId}:{_authenticationOptions.IntrospectionClientSecret}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        }

        HttpResponseMessage response = await _httpClientFactory
            .CreateClient(ClientName)
            .SendAsync(request, Context.RequestAborted).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return AuthenticateResult.Fail("Token introspection failed.");

        using JsonDocument document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(Context.RequestAborted).ConfigureAwait(false),
            cancellationToken: Context.RequestAborted).ConfigureAwait(false);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("active", out JsonElement active) || !active.GetBoolean())
        {
            return AuthenticateResult.Fail("Token is inactive.");
        }

        string userId = ReadString(root, "sub") ?? ReadString(root, "username") ?? "opaque-user";
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, userId),
            new("sub", userId)
        };
        AddStringClaim(root, claims, "tid", "tid");
        AddStringClaim(root, claims, "tenant_id", "tenant_id");
        AddStringClaim(root, claims, "scope", "scope");
        AddStringClaim(root, claims, "roles", "roles");
        AddStringClaim(root, claims, "groups", "groups");

        var identity = new ClaimsIdentity(claims, SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void AddStringClaim(JsonElement root, List<Claim> claims, string propertyName, string claimType)
    {
        string? value = ReadString(root, propertyName);
        if (!string.IsNullOrWhiteSpace(value)) claims.Add(new Claim(claimType, value));
    }
}
