using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Security;
using OpenAgent.Engine.Host.Endpoints.Admin;
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
        [FromServices] IResourceAuthorization authorization,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var agents = await configProvider.ListAgentsAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            cancellationToken).ConfigureAwait(false);
        return await ResourceAccess.FilterAsync(agents, item => item.AgentId, AgentResourceType.Agent, "execute", authorization, context, cancellationToken).ConfigureAwait(false);
    }
}
