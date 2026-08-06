using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Authentication;
using OpenAgent.Hosting.Authentication;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AuthenticationEndpointExtensions
{
    public static IEndpointConventionBuilder MapAuthenticationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/v1/auth");

        group.MapGet("/config", ([FromServices] IOptions<AgentAuthenticationOptions> options) =>
        {
            AgentAuthenticationOptions authentication = options.Value;
            return Results.Ok(new
            {
                password = new
                {
                    enabled = (authentication.Login.Password.Enabled
                        && (!string.IsNullOrWhiteSpace(authentication.Login.Password.TokenEndpoint)
                            || !string.IsNullOrWhiteSpace(authentication.Login.Password.SsoAddress)))
                        || authentication.Providers.Values.Any(item => item.PasswordLoginEnabled),
                    endpoint = "/api/v1/auth/password/token",
                    ssoAddress = authentication.Login.Password.SsoAddress
                },
                providers = authentication.Providers
                    .Where(item => item.Value.PasswordLoginEnabled)
                    .Select(item => new
                    {
                        id = item.Key,
                        authority = item.Value.Authority
                    }),
                microsoft = new
                {
                    enabled = authentication.Login.Microsoft.Enabled
                        && !string.IsNullOrWhiteSpace(authentication.Login.Microsoft.Authority)
                        && !string.IsNullOrWhiteSpace(authentication.Login.Microsoft.ClientId),
                    authority = authentication.Login.Microsoft.Authority,
                    authorizationEndpoint = authentication.Login.Microsoft.AuthorizationEndpoint,
                    clientId = authentication.Login.Microsoft.ClientId,
                    redirectUri = authentication.Login.Microsoft.RedirectUri,
                    scopes = authentication.Login.Microsoft.Scopes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                }
            });
        });

        group.MapPost("/password/token", async (
            [FromServices] IOptions<AgentAuthenticationOptions> options,
            [FromServices] IHttpClientFactory httpClientFactory,
            [FromBody] PasswordLoginRequest request,
            CancellationToken cancellationToken) =>
        {
            AgentAuthenticationOptions authentication = options.Value;
            PasswordLoginOptions password = authentication.Login.Password;
            if (!password.Enabled && string.IsNullOrWhiteSpace(request.SsoAddress))
            {
                return Results.Problem("Password login is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new { error = "username_and_password_required" });
            }

            if (!string.IsNullOrWhiteSpace(request.SsoAddress)
                && !IsConfiguredSsoAddress(authentication, request.SsoAddress))
            {
                return Results.BadRequest(new { error = "sso_provider_not_allowed" });
            }

            PasswordLoginTarget? target = await ResolvePasswordTargetAsync(
                authentication,
                request.SsoAddress,
                httpClientFactory,
                cancellationToken).ConfigureAwait(false);
            if (target == null)
            {
                return Results.Problem("Password SSO token endpoint could not be resolved.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var passwordForm = new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = request.Username,
                ["password"] = request.Password,
                ["client_id"] = target.ClientId,
                ["scope"] = target.Scope
            };
            if (!string.IsNullOrWhiteSpace(target.ClientSecret)) passwordForm["client_secret"] = target.ClientSecret;
            using var content = new FormUrlEncodedContent(passwordForm);
            using HttpResponseMessage response = await httpClientFactory
                .CreateClient("AgentLogin")
                .PostAsync(target.TokenEndpoint, content, cancellationToken)
                .ConfigureAwait(false);
            return await ForwardTokenResponse(response, cancellationToken).ConfigureAwait(false);
        });

        group.MapPost("/microsoft/token", async (
            [FromServices] IOptions<AgentAuthenticationOptions> options,
            [FromServices] IHttpClientFactory httpClientFactory,
            [FromBody] MicrosoftTokenExchangeRequest request,
            CancellationToken cancellationToken) =>
        {
            MicrosoftLoginOptions microsoft = options.Value.Login.Microsoft;
            if (!microsoft.Enabled || string.IsNullOrWhiteSpace(microsoft.Authority))
            {
                return Results.Problem("Microsoft login is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.CodeVerifier))
            {
                return Results.BadRequest(new { error = "code_and_code_verifier_required" });
            }

            string? tokenEndpoint = await ResolveTokenEndpoint(
                microsoft,
                httpClientFactory,
                cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(tokenEndpoint))
            {
                return Results.Problem("Microsoft token endpoint could not be resolved.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            var microsoftForm = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = microsoft.ClientId,
                ["code"] = request.Code,
                ["code_verifier"] = request.CodeVerifier,
                ["redirect_uri"] = request.RedirectUri ?? microsoft.RedirectUri
            };
            if (!string.IsNullOrWhiteSpace(microsoft.ClientSecret)) microsoftForm["client_secret"] = microsoft.ClientSecret;
            using var content = new FormUrlEncodedContent(microsoftForm);
            using HttpResponseMessage response = await httpClientFactory
                .CreateClient("AgentLogin")
                .PostAsync(tokenEndpoint, content, cancellationToken)
                .ConfigureAwait(false);
            return await ForwardTokenResponse(response, cancellationToken).ConfigureAwait(false);
        });

        return group;
    }

    private static async Task<IResult> ForwardTokenResponse(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        return Results.Content(body, mediaType, statusCode: (int)response.StatusCode);
    }

    private static async Task<string?> ResolveTokenEndpoint(
        MicrosoftLoginOptions options,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.TokenEndpoint)) return options.TokenEndpoint;

        string metadataEndpoint = options.Authority.TrimEnd('/') + "/.well-known/openid-configuration";
        using HttpResponseMessage response = await httpClientFactory
            .CreateClient("AgentLogin")
            .GetAsync(metadataEndpoint, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.TryGetProperty("token_endpoint", out JsonElement tokenEndpoint)
            ? tokenEndpoint.GetString()
            : null;
    }

    private static bool IsConfiguredSsoAddress(
        AgentAuthenticationOptions options,
        string address)
    {
        string normalized = Normalize(address);
        if (string.Equals(normalized, Normalize(options.Login.Password.SsoAddress), StringComparison.OrdinalIgnoreCase)) return true;
        return options.Providers.Values.Any(provider =>
            provider.PasswordLoginEnabled
            && (string.Equals(normalized, Normalize(provider.Authority), StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalized, Normalize(provider.TokenEndpoint), StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<PasswordLoginTarget?> ResolvePasswordTargetAsync(
        AgentAuthenticationOptions options,
        string? requestedAddress,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        PasswordLoginTarget target;
        if (!string.IsNullOrWhiteSpace(requestedAddress))
        {
            AuthenticationProviderOptions? provider = options.Providers.Values.FirstOrDefault(item =>
                item.PasswordLoginEnabled
                && (string.Equals(Normalize(item.Authority), Normalize(requestedAddress), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Normalize(item.TokenEndpoint), Normalize(requestedAddress), StringComparison.OrdinalIgnoreCase)));
            if (provider == null) return null;
            target = new PasswordLoginTarget(
                provider.TokenEndpoint,
                provider.Authority,
                provider.ClientId,
                provider.ClientSecret,
                provider.Scope);
        }
        else
        {
            PasswordLoginOptions password = options.Login.Password;
            target = new PasswordLoginTarget(
                password.TokenEndpoint,
                password.SsoAddress,
                password.ClientId,
                password.ClientSecret,
                password.Scope);
        }

        string? endpoint = await ResolveTokenEndpointAsync(
            target.TokenEndpoint,
            target.Authority,
            httpClientFactory,
            cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(endpoint) ? null : target with { TokenEndpoint = endpoint };
    }

    private static async Task<string?> ResolveTokenEndpointAsync(
        string explicitEndpoint,
        string authority,
        IHttpClientFactory httpClientFactory,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(explicitEndpoint)) return explicitEndpoint;
        if (string.IsNullOrWhiteSpace(authority)) return null;

        using HttpResponseMessage response = await httpClientFactory
            .CreateClient("AgentLogin")
            .GetAsync(authority.TrimEnd('/') + "/.well-known/openid-configuration", cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using JsonDocument document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return document.RootElement.TryGetProperty("token_endpoint", out JsonElement tokenEndpoint)
            ? tokenEndpoint.GetString()
            : null;
    }

    private static string Normalize(string? value) => value?.Trim().TrimEnd('/') ?? string.Empty;

    private sealed record PasswordLoginTarget(
        string TokenEndpoint,
        string Authority,
        string ClientId,
        string ClientSecret,
        string Scope);
}
