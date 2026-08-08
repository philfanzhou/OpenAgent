using System.Text;
using Microsoft.Extensions.Options;
using OpenAgent.Contracts.Authentication;
using OpenAgent.Hosting.Authentication;

namespace OpenAgent.Router.Endpoints;

internal static class GatewayAuthenticationEndpointHandler
{
    internal static IResult GetConfig(IOptions<AgentAuthenticationOptions> options)
    {
        AgentAuthenticationOptions authentication = options.Value;
        return Results.Ok(new
        {
            mode = authentication.Mode.ToString(),
            authority = authentication.Mode == AgentAuthenticationMode.JwtBearer
                ? authentication.Authority
                : null,
            audience = authentication.Mode == AgentAuthenticationMode.JwtBearer
                ? authentication.Audience
                : null,
            password = new
            {
                enabled = authentication.Mode == AgentAuthenticationMode.Basic,
                endpoint = authentication.Mode == AgentAuthenticationMode.Basic
                    ? "/api/v1/auth/password/token"
                    : null
            }
        });
    }

    internal static IResult CreateDevelopmentBasicCredential(
        PasswordLoginRequest request,
        IOptions<AgentAuthenticationOptions> options,
        IHostEnvironment environment)
    {
        AgentAuthenticationOptions authentication = options.Value;
        if (authentication.Mode != AgentAuthenticationMode.Basic
            || !authentication.AllowDevelopmentAnonymous
            || !environment.IsDevelopment())
        {
            return Results.NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return Results.BadRequest(new { error = "username_and_password_required" });
        }

        string basicCredential = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{request.Username}:{request.Password}"));
        return Results.Ok(new
        {
            access_token = basicCredential,
            token_type = "Basic",
            user_id = request.Username
        });
    }
}
