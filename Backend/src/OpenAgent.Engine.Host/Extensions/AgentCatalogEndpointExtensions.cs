using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Security;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AgentCatalogEndpointExtensions
{
    internal static void MapAgentCatalog(this RouteGroupBuilder group)
    {
        group.MapGet("/agents", ExecuteAsync)
            .RequireAuthorization(GatewayPermissions.AgentRead)
            .WithName("ListAgents")
            .WithTags("Agent");
    }

    private static async Task<IResult> ExecuteAsync(
        [FromServices] IAgentConfigProvider configProvider,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentSummary> agents = await configProvider.ListAgentsAsync(
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(agents);
    }
}
