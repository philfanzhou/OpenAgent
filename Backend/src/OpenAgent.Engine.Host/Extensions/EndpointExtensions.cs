using OpenAgent.Contracts.Responses;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Endpoints.Agents;
using OpenAgent.Engine.Host.Endpoints.Chat;
using OpenAgent.Engine.Host.Endpoints.Conversations;
using OpenAgent.Engine.Host.Endpoints.Files;
using OpenAgent.Engine.Host.Endpoints.Provider;
using OpenAgent.Engine.Host.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace OpenAgent.Engine.Host.Extensions;

internal static class EndpointExtensions
{
    public static IEndpointConventionBuilder MapAgentEndpoints(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/api/v1/agent")
    {
        RouteGroupBuilder group = endpoints.MapGroup(pattern).RequireAuthorization();
        group.MapAgentChat();
        group.MapFileAssets();
        group.MapAgentCatalog();
        group.MapAgentProviderContract();
        group.MapConversations();

        group.MapGet("/runtimes", ([FromServices] IConfiguredRuntimeProfiles? profiles, HttpContext context) =>
        {
            string tenantId = Endpoints.AgentEndpointRequestMapper.RequireTenant(context);
            IReadOnlyList<RuntimeProfile> configured = profiles?.ListForTenant(tenantId) ?? [];
            return TypedResults.Ok(configured);
        }).WithName("AvailableAgentRuntimes").WithTags("Agent");

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
