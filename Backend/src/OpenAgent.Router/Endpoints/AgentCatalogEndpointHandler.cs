using OpenAgent.Contracts.Security;
using OpenAgent.Router.Models;

namespace OpenAgent.Router.Endpoints;

internal static class AgentCatalogEndpointHandler
{
    internal static async Task<IResult> HandleAsync(
        HttpContext context,
        IAgentUserContext userContext,
        IRouteTable routeTable,
        IAgentCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (!userContext.IsAuthenticated)
        {
            return Results.Unauthorized();
        }

        string? tenantId = userContext.TenantId
            ?? context.Request.Headers["X-Tenant-Id"].FirstOrDefault();
        string? engineEndpoint = routeTable.GetTargetEndpoint(
            "chat",
            tenantId,
            conversationId: null);
        if (string.IsNullOrWhiteSpace(engineEndpoint))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "No Engine is available");
        }

        DownstreamRequestIdentity identity = new(
            context.Request.Headers.Authorization.FirstOrDefault(),
            tenantId,
            context.Request.Headers["X-Agent-Audience"].FirstOrDefault(),
            context.Request.Headers["X-Trace-Id"].FirstOrDefault()
                ?? context.TraceIdentifier);
        IReadOnlyList<RoutableAgent> agents = await catalog.ListAsync(
            new AgentCatalogRequest(
                engineEndpoint,
                identity,
                userContext,
                IntentCandidatesOnly: false),
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(agents.Select(agent => agent.Summary));
    }
}
