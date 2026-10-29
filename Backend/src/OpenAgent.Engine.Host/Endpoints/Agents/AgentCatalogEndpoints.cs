using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Host.Endpoints;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Endpoints.Agents;

internal static class AgentCatalogEndpoints
{
    internal static void MapAgentCatalog(this RouteGroupBuilder group)
    {
        group.MapGet("/agents", ExecuteAsync)
            .WithName("ListAgents")
            .WithTags("Agent");
    }

    private static async Task<IReadOnlyList<AgentSummary>> ExecuteAsync(
        [FromServices] IAgentConfigProvider configProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        return await configProvider.ListAgentsAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            cancellationToken).ConfigureAwait(false);
    }
}
