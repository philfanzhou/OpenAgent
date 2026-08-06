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
                    enabled = authentication.Login.Password.Enabled
                        && !string.IsNullOrWhiteSpace(authentication.Login.Password.TokenEndpoint),
                    endpoint = "/api/v1/auth/password/token"
                },
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
            PasswordLoginOptions password = options.Value.Login.Password;
            if (!password.Enabled || string.IsNullOrWhiteSpace(password.TokenEndpoint))
            {
                return Results.Problem("Password login is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new { error = "username_and_password_required" });
            }

            var passwordForm = new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["username"] = request.Username,
                ["password"] = request.Password,
                ["client_id"] = password.ClientId,
                ["scope"] = password.Scope
            };
            if (!string.IsNullOrWhiteSpace(password.ClientSecret)) passwordForm["client_secret"] = password.ClientSecret;
            using var content = new FormUrlEncodedContent(passwordForm);
            using HttpResponseMessage response = await httpClientFactory
                .CreateClient("AgentLogin")
                .PostAsync(password.TokenEndpoint, content, cancellationToken)
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
}
