using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AgentCatalogEndpointExtensions
{
    internal static void MapAgentCatalog(this RouteGroupBuilder group)
    {
        group.MapGet("/agents", ExecuteAsync)
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
