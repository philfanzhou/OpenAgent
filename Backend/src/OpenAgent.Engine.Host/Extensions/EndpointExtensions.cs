using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        group.MapFileAssets();
        group.MapAgentCatalog();
        group.MapAgentProviderContract();
        group.MapConversations();
        group.MapHumanApprovals();

        group.MapGet("/me", async (
            HttpContext context,
            [FromServices] IAuthorizationService authorization) =>
        {
            IAgentUserContext user = context.GetAgentRequest().User;
            AuthorizationResult approval = await authorization.AuthorizeAsync(
                context.User,
                resource: null,
                policyName: "approval.decide").ConfigureAwait(false);
            return Results.Ok(new
            {
                userId = user.UserId,
                tenantId = user.TenantId,
                roles = user.Roles,
                groups = user.Groups,
                audience = user.Audience,
                isAuthenticated = user.IsAuthenticated,
                canDecideApprovals = approval.Succeeded
            });
        })
        .WithName("CurrentAgentUser")
        .WithTags("Agent");

        return group;
    }
}
