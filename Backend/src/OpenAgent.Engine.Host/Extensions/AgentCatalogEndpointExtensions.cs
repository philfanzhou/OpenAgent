using Microsoft.AspNetCore.Mvc;
using OpenAgent.Contracts.Configuration;
using OpenAgent.Contracts.Runtime;
using OpenAgent.Engine.Host.Middleware;

namespace OpenAgent.Engine.Host.Extensions;

internal static class AgentCatalogEndpointExtensions
{
    internal static void MapAgentCatalog(this RouteGroupBuilder group)
    {
        group.MapGet("/agents", ExecuteAsync)
            .WithName("ListAgents")
            .WithTags("Agent");
    }

    private static async Task<IReadOnlyList<AgentSummary>> ExecuteAsync(
        [FromServices] IAgentConfigProvider configProvider,
        [FromServices] IRuntimeCatalog runtimes,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AgentSummary> agents = await configProvider.ListAgentsAsync(
            AgentEndpointRequestMapper.RequireTenant(context),
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<RuntimeDescriptor> choices = runtimes.List();
        foreach (AgentSummary agent in agents)
            agent.RequiresLlmProfile = choices.FirstOrDefault(item => item.Adapter == agent.Runtime.Adapter
                && item.Harness == agent.Runtime.Harness)?.RequiresLlmProfile ?? true;
        return agents;
    }
}
