using Microsoft.AspNetCore.Mvc;
using OpenAgent.Authorization;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AgentCatalogEndpointExtensions
{
    internal static void MapAgentCatalog(this RouteGroupBuilder group)
    {
        group.MapGet("/agents", ExecuteAsync)
            .RequireAuthorization(PermissionCatalog.AgentRead)
            .WithName("ListAgents")
            .WithTags("Agent");
    }

    private static async Task<IResult> ExecuteAsync(
        [FromServices] IAgentConfigProvider configProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentSummary> agents = await configProvider.ListAgentsAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(agents);
    }
}
