using System.Text;
using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Authentication;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AuthenticationEndpointExtensions
{
    public static IEndpointConventionBuilder MapAuthenticationEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder group = endpoints.MapGroup("/api/v1/auth");

        group.MapGet("/config", () => Results.Ok(new
        {
            mode = "Basic",
            password = new
            {
                enabled = true,
                endpoint = "/api/v1/auth/password/token"
            }
        }));

        group.MapPost("/password/token", (
            [FromBody] PasswordLoginRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username)
                || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new { error = "username_and_password_required" });
            }

            // This is intentionally not a credential verifier. The current
            // phase only establishes an identity; authorization is separate.
            string basicCredential = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{request.Username}:{request.Password}"));
            return Results.Ok(new
            {
                access_token = basicCredential,
                token_type = "Basic",
                user_id = request.Username
            });
        });

        return group;
    }
}
