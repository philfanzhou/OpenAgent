using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Responses;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class EndpointExtensions
{
    public static IEndpointConventionBuilder MapAgentEndpoints(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/api/v1/agent")
    {
        RouteGroupBuilder group = endpoints.MapGroup(pattern).RequireAuthorization();
        group.MapGet("/runtimes", ([FromServices] IRuntimeCatalog catalog) => catalog.List()).WithName("ListRuntimes").WithTags("Agent");
        group.MapPost("/runtimes/{harness}/test", (string harness, [FromServices] IRuntimeDiagnostics diagnostics,
            CancellationToken cancellationToken) => diagnostics.TestAsync(harness, cancellationToken)).WithName("TestRuntime").WithTags("Agent");
        group.MapAgentChat();
        group.MapFileAssets();
        group.MapAgentCatalog();
        group.MapAgentProviderContract();
        group.MapConversations();

        group.MapGet("/me", (HttpContext context) =>
        {
            IAgentUserContext user = context.GetAgentRequest().User;
            return TypedResults.Ok(new MeResponse
            {
                UserId = user.UserId,
                Username = user.Username,
                Email = user.Email,
                TenantId = user.TenantId,
                Roles = user.Roles,
                Groups = user.Groups,
                Claims = user.Claims,
                Audience = user.Audience,
                IsAuthenticated = user.IsAuthenticated
            });
        })
        .WithName("CurrentAgentUser")
        .WithTags("Agent");

        return group;
    }
}
