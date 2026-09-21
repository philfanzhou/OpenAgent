using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Authentication;
using OpenAgent.Contracts.Responses;
using OpenAgent.Hosting.Errors;
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

        group.MapGet("/config", () => TypedResults.Ok(new AuthConfigResponse
        {
            Mode = options.Mode.ToString(),
            Development = environment.IsDevelopment(),
            Keycloak = new KeycloakConfigResponse { Enabled = options.EnableKeycloak },
            Password = new PasswordAuthConfigResponse
            {
                Enabled = environment.IsDevelopment() && options.Mode == AgentAuthenticationMode.Basic,
                Endpoint = "/api/v1/auth/password/token"
            },
            Anonymous = new AnonymousAuthConfigResponse
            {
                Enabled = environment.IsDevelopment()
                    && options.Mode == AgentAuthenticationMode.Basic
                    && options.AllowDevelopmentAnonymous
            },
            Oidc = options.Mode == AgentAuthenticationMode.JwtBearer
                ? new OidcConfigResponse
                {
                    Authority = options.Authority,
                    ClientId = options.ClientId,
                    Audience = options.Audience,
                    Scopes = options.Scopes.Length == 0 ? ["openid", "profile"] : options.Scopes
                }
                : null
        }))
            .AllowAnonymous()
            .WithName("GetAuthConfig")
            .WithTags("Authentication")
            .WithSummary("获取认证配置");

        if (environment.IsDevelopment() && options.Mode == AgentAuthenticationMode.Basic)
        {
            group.MapPost("/password/token",
                Results<Ok<TokenResponse>, ProblemHttpResult> (PasswordLoginRequest request, HttpContext context) =>
            {
                if (string.IsNullOrWhiteSpace(request.Username)
                    || string.IsNullOrWhiteSpace(request.Password))
                {
                    return TypedResults.Problem(AgentProblemDetails.Invalid(
                        "username_and_password_required", context));
                }

                if (!DevelopmentCredentials.IsValid(request.Username, request.Password))
                {
                    return TypedResults.Problem(AgentProblemDetails.AuthenticationRequired(
                        "Invalid username or password.", context));
                }

                string basicCredential = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{request.Username}:{request.Password}"));
                return TypedResults.Ok(new TokenResponse
                {
                    AccessToken = basicCredential,
                    TokenType = BasicAuthenticationHandler.SchemeName
                });
            })
                .AllowAnonymous()
                .WithName("IssuePasswordToken")
                .WithTags("Authentication")
                .WithSummary("开发环境密码登录换发令牌");
        }

        return endpoints;
    }
}
