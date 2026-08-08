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
        group.MapAgentChat();
        group.MapAttachmentChat();
        group.MapAgentCatalog();
        group.MapConversations();

        group.MapGet("/me", (HttpContext context) =>
        {
            IAgentUserContext user = context.GetAgentRequest().User;
            return Results.Ok(new
            {
                userId = user.UserId,
                tenantId = user.TenantId,
                roles = user.Roles,
                groups = user.Groups,
                audience = user.Audience,
                isAuthenticated = user.IsAuthenticated
            });
        })
        .RequireAuthorization(GatewayPermissions.IdentityRead)
        .WithName("CurrentAgentUser")
        .WithTags("Agent");

        return group;
    }
}
