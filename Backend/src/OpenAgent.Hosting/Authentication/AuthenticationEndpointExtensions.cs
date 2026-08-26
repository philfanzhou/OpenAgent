using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenAgent.Hosting.Security;

namespace OpenAgent.Hosting.Authentication;

public static class AuthenticationEndpointExtensions
{
    public static IEndpointRouteBuilder MapAgentAuthenticationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        IHostEnvironment environment = endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>();
        AgentAuthenticationOptions options = endpoints.ServiceProvider
            .GetRequiredService<IOptions<AgentAuthenticationOptions>>().Value;
        RouteGroupBuilder group = endpoints.MapGroup("/api/v1/auth");

        group.MapGet("/config", () => Results.Ok(new
        {
            mode = options.Mode.ToString(),
            development = environment.IsDevelopment(),
            domainLogin = new
            {
                enabled = options.EnableDomainLogin
            },
            tenant = new
            {
                enabled = options.EnableTenant
            },
            password = new
            {
                enabled = environment.IsDevelopment() && options.Mode == AgentAuthenticationMode.Basic,
                endpoint = "/api/v1/auth/password/token"
            },
            anonymous = new
            {
                enabled = environment.IsDevelopment()
                    && options.Mode == AgentAuthenticationMode.Basic
                    && options.AllowDevelopmentAnonymous
            },
            oidc = options.Mode == AgentAuthenticationMode.JwtBearer
                ? new
                {
                    authority = options.Authority,
                    clientId = options.ClientId,
                    audience = options.Audience,
                    scopes = options.Scopes.Length == 0
                        ? ["openid", "profile"]
                        : options.Scopes
                }
                : null
        })).AllowAnonymous();

        if (environment.IsDevelopment() && options.Mode == AgentAuthenticationMode.Basic)
        {
            group.MapPost("/password/token", (PasswordLoginRequest request) =>
            {
                if (string.IsNullOrWhiteSpace(request.Username)
                    || string.IsNullOrWhiteSpace(request.Password))
                {
                    return Results.BadRequest(new { error = "username_and_password_required" });
                }

                if (!DevelopmentCredentials.IsValid(request.Username, request.Password))
                {
                    return Results.Unauthorized();
                }

                string basicCredential = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{request.Username}:{request.Password}"));
                return Results.Ok(new
                {
                    access_token = basicCredential,
                    token_type = BasicAuthenticationHandler.SchemeName
                });
            }).AllowAnonymous();
        }

        return endpoints;
    }

    private sealed class PasswordLoginRequest
    {
        public string Username { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
    }
}
