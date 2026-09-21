using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AgentCatalogEndpointExtensions
{
    internal static void MapAgentCatalog(this RouteGroupBuilder group)
    {
        group.MapGet("/agents", ExecuteAsync)
            .WithName("ListAgents")
            .WithTags("Agent")
            .WithSummary("列出可用 Agent");
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
